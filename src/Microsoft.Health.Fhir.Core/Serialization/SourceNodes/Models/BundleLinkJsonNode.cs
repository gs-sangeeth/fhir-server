﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Microsoft.Health.Fhir.Core.Serialization.SourceNodes.Models
{
    [SuppressMessage("Design", "CA2227", Justification = "POCO style model")]
    [SuppressMessage("Design", "CA1056", Justification = "POCO style model")]
    public class BundleLinkJsonNode
    {
        [JsonPropertyName("relation")]
        public string Relation { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; }
    }
}
