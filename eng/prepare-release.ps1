$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" }
}

Push-Location $repo
try {
    # No publishing or credentials are involved in this script.
    $manifest = Join-Path $repo 'artifacts/packages/release-manifest.json'
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
    Invoke-DotNet restore ZeroMapper.sln --disable-parallel
    Invoke-DotNet clean ZeroMapper.sln -c Release --nologo '-m:1' -v quiet
    Invoke-DotNet build ZeroMapper.sln -c Release --no-restore --nologo '-m:1'
    Invoke-DotNet test tests/ZeroMapper.Tests/ZeroMapper.Tests.csproj -c Release --no-build --no-restore --nologo --logger 'trx;LogFileName=prerelease.trx' --results-directory artifacts/test-results --blame-hang-timeout 60s
    & (Join-Path $PSScriptRoot 'verify-packages.ps1')
    if (-not (Test-Path -LiteralPath $manifest)) { throw 'Package verification did not produce a manifest.' }
    $evidence = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $report = Join-Path $repo 'artifacts/test-results/prerelease.trx'
    $evidence | Add-Member -NotePropertyName testReport -NotePropertyValue ([ordered]@{
        file = 'artifacts/test-results/prerelease.trx'
        sha256 = (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()
        result = 'passed'
    })
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifest
    Write-Output 'Local prerelease verification passed. No packages were published.'
} finally { Pop-Location }
