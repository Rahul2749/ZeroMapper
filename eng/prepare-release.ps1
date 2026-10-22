$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$properties = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Invalid package release version.' }
$reportName = "release-$version.trx"

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" }
}

Push-Location $repo
try {
    # No publishing or credentials are involved in this script.
    $manifest = Join-Path $repo "artifacts/packages/$version/release-manifest.json"
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
    Invoke-DotNet restore ZeroMapper.sln --disable-parallel
    Invoke-DotNet clean ZeroMapper.sln -c Release --nologo '-m:1' -v quiet
    Invoke-DotNet build ZeroMapper.sln -c Release --no-restore --nologo '-m:1'
    Invoke-DotNet test tests/ZeroMapper.Tests/ZeroMapper.Tests.csproj -c Release --no-build --no-restore --nologo --logger "trx;LogFileName=$reportName" --results-directory artifacts/test-results --blame-hang-timeout 60s
    & (Join-Path $PSScriptRoot 'verify-packages.ps1')
    if (-not (Test-Path -LiteralPath $manifest)) { throw 'Package verification did not produce a manifest.' }
    $evidence = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $report = Join-Path $repo "artifacts/test-results/$reportName"
    $evidence | Add-Member -NotePropertyName testReport -NotePropertyValue ([ordered]@{
        file = "artifacts/test-results/$reportName"
        sha256 = (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()
        result = 'passed'
    })
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest
    Write-Output "Local release verification passed for $version. No packages were published."
} finally { Pop-Location }
