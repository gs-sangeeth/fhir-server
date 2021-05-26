$buildLocator=$args[0]

$headers = @{
    Authorization="Bearer eyJ0eXAiOiAiVENWMiJ9.ZzFicHh3NVBYeVJVZWgtSGROc0ZsWTRPWmw0.OWQ1N2JhNjMtMTNlNy00ODI2LThkY2UtOWMyNDUxNTZlODYz"
}
[xml]$buildInfo = (Invoke-WebRequest -URI http://localhost:3500/app/rest/builds/$buildLocator -UseBasicParsing -Headers $headers).Content

$branchName = $buildInfo.build.revisions.revision.vcsBranchName
$buildConfigId = $buildInfo.build.buildType.id
$buildConfigName = $buildInfo.build.buildType.name
$buildNumber = $buildInfo.build.number
[String[]]$tags = @()
foreach ($tag in $buildInfo.build.tags.tag) {
    [String[]]$tags+= $tag.name
}

Write-Output $branchName, $buildConfigId, $buildConfigName, $buildNumber, $tags

# [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
# $headers = @{
#     Accept="application/json"
#     Authorization="Bearer eyJ0eXAiOiAiVENWMiJ9.ZzFicHh3NVBYeVJVZWgtSGROc0ZsWTRPWmw0.OWQ1N2JhNjMtMTNlNy00ODI2LThkY2UtOWMyNDUxNTZlODYz"
# }
# $Response = (Invoke-WebRequest -URI http://localhost:3500/app/rest/builds/1 -Headers $headers -ContentType "application/json").Content | ConvertFrom-Json
# $Response.revisions
# $Response = (Invoke-WebRequest -URI http://localhost:3500/app/rest/projects/FhirServer -Headers $headers -ContentType "application/json").Content | ConvertFrom-Json | ConvertTo-Json

# $Response = Invoke-WebRequest -URI https://www.bing.com/search?q=how+many+feet+in+a+mile
# $Response.InputFields | Where-Object {
#     $_.name -like "* Value*"
# } | Select-Object Name, Value
