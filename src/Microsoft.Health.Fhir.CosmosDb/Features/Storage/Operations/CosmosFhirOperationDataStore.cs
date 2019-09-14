﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Abstractions.Exceptions;
using Microsoft.Health.CosmosDb.Configs;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Operations.Export.Models;
using Microsoft.Health.Fhir.Core.Features.Operations.Import.Models;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations.Export;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations.Import;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.AcquireExportJobs;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.StoredProcedures.AcquireImportJobs;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage.Operations
{
    public sealed class CosmosFhirOperationDataStore : IFhirOperationDataStore
    {
        private const string HashParameterName = "@hash";

        private static readonly string GetJobByHashQuery =
            $"SELECT TOP 1 * FROM ROOT r WHERE r.{JobRecordProperties.JobRecord}.{JobRecordProperties.Hash} = {HashParameterName} AND r.{JobRecordProperties.JobRecord}.{JobRecordProperties.Status} IN ('{OperationStatus.Queued}', '{OperationStatus.Running}') ORDER BY r.{KnownDocumentProperties.Timestamp} ASC";

        private readonly IScoped<IDocumentClient> _documentClientScope;
        private readonly RetryExceptionPolicyFactory _retryExceptionPolicyFactory;
        private readonly ILogger _logger;

        private readonly AcquireExportJobs _acquireExportJobs = new AcquireExportJobs();
        private readonly AcquireImportJobs _getAvailableImportJobs = new AcquireImportJobs();

        /// <summary>
        /// Initializes a new instance of the <see cref="CosmosFhirOperationDataStore"/> class.
        /// </summary>
        /// <param name="documentClientScope">The factory for <see cref="IDocumentClient"/>.</param>
        /// <param name="cosmosDataStoreConfiguration">The data store configuration.</param>
        /// <param name="namedCosmosCollectionConfigurationAccessor">The IOptions accessor to get a named version.</param>
        /// <param name="retryExceptionPolicyFactory">The retry exception policy factory.</param>
        /// <param name="logger">The logger.</param>
        public CosmosFhirOperationDataStore(
            IScoped<IDocumentClient> documentClientScope,
            CosmosDataStoreConfiguration cosmosDataStoreConfiguration,
            IOptionsMonitor<CosmosCollectionConfiguration> namedCosmosCollectionConfigurationAccessor,
            RetryExceptionPolicyFactory retryExceptionPolicyFactory,
            ILogger<CosmosFhirOperationDataStore> logger)
        {
            EnsureArg.IsNotNull(documentClientScope, nameof(documentClientScope));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(namedCosmosCollectionConfigurationAccessor, nameof(namedCosmosCollectionConfigurationAccessor));
            EnsureArg.IsNotNull(retryExceptionPolicyFactory, nameof(retryExceptionPolicyFactory));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _documentClientScope = documentClientScope;
            _retryExceptionPolicyFactory = retryExceptionPolicyFactory;
            _logger = logger;

            CosmosCollectionConfiguration collectionConfiguration = namedCosmosCollectionConfigurationAccessor.Get(Constants.CollectionConfigurationName);

            DatabaseId = cosmosDataStoreConfiguration.DatabaseId;
            CollectionId = collectionConfiguration.CollectionId;
            CollectionUri = cosmosDataStoreConfiguration.GetRelativeCollectionUri(collectionConfiguration.CollectionId);
        }

        private string DatabaseId { get; }

        private string CollectionId { get; }

        private Uri CollectionUri { get; }

        public async Task<ExportJobOutcome> CreateExportJobAsync(ExportJobRecord jobRecord, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosExportJob = new CosmosExportJobRecordWrapper(jobRecord);

            try
            {
                ResourceResponse<Document> result = await _documentClientScope.Value.CreateDocumentAsync(
                    CollectionUri,
                    cosmosExportJob,
                    new RequestOptions() { PartitionKey = new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey) },
                    disableAutomaticIdGeneration: true,
                    cancellationToken: cancellationToken);

                return new ExportJobOutcome(jobRecord, WeakETag.FromVersionId(result.Resource.ETag));
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Failed to create an export job.");
                throw;
            }
        }

        public async Task<ExportJobOutcome> GetExportJobByIdAsync(string id, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(id, nameof(id));

            try
            {
                DocumentResponse<CosmosExportJobRecordWrapper> cosmosExportJobRecord = await _documentClientScope.Value.ReadDocumentAsync<CosmosExportJobRecordWrapper>(
                    UriFactory.CreateDocumentUri(DatabaseId, CollectionId, id),
                    new RequestOptions { PartitionKey = new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey) },
                    cancellationToken);

                var outcome = new ExportJobOutcome(cosmosExportJobRecord.Document.JobRecord, WeakETag.FromVersionId(cosmosExportJobRecord.Document.ETag));

                return outcome;
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, id));
                }

                _logger.LogError(dce, "Failed to get an export job by id.");
                throw;
            }
        }

        public async Task<ExportJobOutcome> GetExportJobByHashAsync(string hash, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(hash, nameof(hash));

            try
            {
                IDocumentQuery<CosmosExportJobRecordWrapper> query = _documentClientScope.Value.CreateDocumentQuery<CosmosExportJobRecordWrapper>(
                    CollectionUri,
                    new SqlQuerySpec(
                       GetJobByHashQuery,
                       new SqlParameterCollection()
                       {
                           new SqlParameter(HashParameterName, hash),
                       }),
                    new FeedOptions { PartitionKey = new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey) })
                    .AsDocumentQuery();

                FeedResponse<CosmosExportJobRecordWrapper> result = await query.ExecuteNextAsync<CosmosExportJobRecordWrapper>();

                if (result.Count == 1)
                {
                    // We found an existing job that matches the hash.
                    CosmosExportJobRecordWrapper wrapper = result.First();

                    return new ExportJobOutcome(wrapper.JobRecord, WeakETag.FromVersionId(wrapper.ETag));
                }

                return null;
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Failed to get an export job by hash.");
                throw;
            }
        }

        public async Task<ExportJobOutcome> UpdateExportJobAsync(ExportJobRecord jobRecord, WeakETag eTag, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosExportJob = new CosmosExportJobRecordWrapper(jobRecord);

            var requestOptions = new RequestOptions()
            {
                PartitionKey = new PartitionKey(CosmosDbExportConstants.ExportJobPartitionKey),
            };

            // Create access condition so that record is replaced only if eTag matches.
            if (eTag != null)
            {
                requestOptions.AccessCondition = new AccessCondition()
                {
                    Type = AccessConditionType.IfMatch,
                    Condition = eTag.VersionId,
                };
            }

            try
            {
                ResourceResponse<Document> replaceResult = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    () => _documentClientScope.Value.ReplaceDocumentAsync(
                        UriFactory.CreateDocumentUri(DatabaseId, CollectionId, jobRecord.Id),
                        cosmosExportJob,
                        requestOptions,
                        cancellationToken));

                var latestETag = replaceResult.Resource.ETag;
                return new ExportJobOutcome(jobRecord, WeakETag.FromVersionId(latestETag));
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new JobConflictException();
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, jobRecord.Id));
                }

                _logger.LogError(dce, "Failed to update an export job.");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<ExportJobOutcome>> AcquireExportJobsAsync(
            ushort maximumNumberOfConcurrentJobsAllowed,
            TimeSpan jobHeartbeatTimeoutThreshold,
            CancellationToken cancellationToken)
        {
            try
            {
                StoredProcedureResponse<IReadOnlyCollection<CosmosExportJobRecordWrapper>> response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _acquireExportJobs.ExecuteAsync(
                        _documentClientScope.Value,
                        CollectionUri,
                        maximumNumberOfConcurrentJobsAllowed,
                        (ushort)jobHeartbeatTimeoutThreshold.TotalSeconds,
                        ct),
                    cancellationToken);

                return response.Response.Select(wrapper => new ExportJobOutcome(wrapper.JobRecord, WeakETag.FromVersionId(wrapper.ETag))).ToList();
            }
            catch (DocumentClientException dce)
            {
                if (dce.GetSubStatusCode() == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(null);
                }

                _logger.LogError(dce, "Failed to acquire export jobs.");
                throw;
            }
        }

        public async Task<ImportJobOutcome> CreateImportJobAsync(ImportJobRecord jobRecord, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosImportJob = new CosmosImportJobRecordWrapper(jobRecord);

            try
            {
                ResourceResponse<Document> result = await _documentClientScope.Value.CreateDocumentAsync(
                    CollectionUri,
                    cosmosImportJob,
                    new RequestOptions() { PartitionKey = new PartitionKey(CosmosDbImportConstants.ImportJobPartitionKey) },
                    disableAutomaticIdGeneration: true,
                    cancellationToken: cancellationToken);

                return new ImportJobOutcome(jobRecord, WeakETag.FromVersionId(result.Resource.ETag));
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }

                _logger.LogError(dce, "Unhandled Document Client Exception");
                throw;
            }
        }

        public async Task<ImportJobOutcome> GetImportJobAsync(string jobId, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(jobId);

            try
            {
                DocumentResponse<CosmosImportJobRecordWrapper> cosmosImportJobRecord = await _documentClientScope.Value.ReadDocumentAsync<CosmosImportJobRecordWrapper>(
                    UriFactory.CreateDocumentUri(DatabaseId, CollectionId, jobId),
                    new RequestOptions { PartitionKey = new PartitionKey(CosmosDbImportConstants.ImportJobPartitionKey) },
                    cancellationToken);

                var eTagHeaderValue = cosmosImportJobRecord.ResponseHeaders["ETag"];
                var outcome = new ImportJobOutcome(cosmosImportJobRecord.Document.JobRecord, WeakETag.FromVersionId(eTagHeaderValue));

                return outcome;
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, jobId));
                }

                _logger.LogError(dce, "Unhandled Document Client Exception");
                throw;
            }
        }

        public async Task<ImportJobOutcome> UpdateImportJobAsync(ImportJobRecord jobRecord, WeakETag eTag, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(jobRecord, nameof(jobRecord));

            var cosmosExportJob = new CosmosImportJobRecordWrapper(jobRecord);

            var requestOptions = new RequestOptions()
            {
                PartitionKey = new PartitionKey(CosmosDbImportConstants.ImportJobPartitionKey),
            };

            // Create access condition so that record is replaced only if eTag matches.
            if (eTag != null)
            {
                requestOptions.AccessCondition = new AccessCondition()
                {
                    Type = AccessConditionType.IfMatch,
                    Condition = eTag.VersionId,
                };
            }

            try
            {
                ResourceResponse<Document> replaceResult = await _documentClientScope.Value.ReplaceDocumentAsync(
                    UriFactory.CreateDocumentUri(DatabaseId, CollectionId, jobRecord.Id),
                    cosmosExportJob,
                    requestOptions,
                    cancellationToken: cancellationToken);

                var latestETag = replaceResult.Resource.ETag;
                return new ImportJobOutcome(jobRecord, WeakETag.FromVersionId(latestETag));
            }
            catch (DocumentClientException dce)
            {
                if (dce.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new RequestRateExceededException(dce.RetryAfter);
                }
                else if (dce.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    throw new JobConflictException();
                }
                else if (dce.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new JobNotFoundException(string.Format(Core.Resources.JobNotFound, jobRecord.Id));
                }

                _logger.LogError(dce, "Unhandled Document Client Exception");
                throw;
            }
        }

        public async Task<IReadOnlyCollection<ImportJobOutcome>> AcquireImportJobsAsync(
            ushort maximumNumberOfConcurrentJobsAllowed,
            TimeSpan jobHeartbeatTimeoutThreshold,
            CancellationToken cancellationToken)
        {
            try
            {
                StoredProcedureResponse<IReadOnlyCollection<CosmosImportJobRecordWrapper>> response = await _retryExceptionPolicyFactory.CreateRetryPolicy().ExecuteAsync(
                    async ct => await _getAvailableImportJobs.ExecuteAsync(
                        _documentClientScope.Value,
                        CollectionUri,
                        maximumNumberOfConcurrentJobsAllowed,
                        (ushort)jobHeartbeatTimeoutThreshold.TotalSeconds,
                        ct),
                    cancellationToken);

                return response.Response.Select(wrapper => new ImportJobOutcome(wrapper.JobRecord, WeakETag.FromVersionId(wrapper.ETag))).ToList();
            }
            catch (DocumentClientException dce)
            {
                string subStatusInString = dce.ResponseHeaders.Get(CosmosDbHeaders.SubStatus);

                if (!string.IsNullOrEmpty(subStatusInString) &&
                    int.TryParse(subStatusInString, NumberStyles.Integer, CultureInfo.InvariantCulture, out int subStatus))
                {
                    if (subStatus == (int)HttpStatusCode.TooManyRequests)
                    {
                        throw new RequestRateExceededException(null);
                    }
                }

                _logger.LogError(dce, "Unhandled Document Client Exception");
                throw;
            }
        }
    }
}
