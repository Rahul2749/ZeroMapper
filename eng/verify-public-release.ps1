param([string]$Version)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$consumer = Join-Path $repo ('artifacts/public-consumer/' + [Guid]::NewGuid().ToString('N'))
if (-not $Version) {
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
    $Version = [string]$properties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Invalid release version.' }

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" }
}

New-Item -ItemType Directory -Force -Path $consumer | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <RestorePackagesPath>packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="ZeroMapper" Version="[$version]" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj')
$guide = Get-Content -LiteralPath (Join-Path $repo 'docs/usage-guide.md') -Raw
$example = [regex]::Match($guide, '(?s)<!-- verified-quickstart -->\s*```csharp\s*\r?\n(.*?)\r?\n```')
if (-not $example.Success) { throw 'Usage example not found.' }
$example.Groups[1].Value | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
$project = Join-Path $consumer 'Consumer.csproj'
Invoke-DotNet restore $project --source https://api.nuget.org/v3/index.json --no-cache
Invoke-DotNet build $project -c Release --no-restore '-m:1'
foreach ($framework in 'net8.0', 'net10.0') {
    Invoke-DotNet run --project $project -c Release --framework $framework --no-build --no-restore
    if (Test-Path -LiteralPath (Join-Path $consumer "bin/Release/$framework/ZeroMapper.Generator.dll")) {
        throw 'Generator leaked into application runtime output.'
    }
}
[ordered]@{
    version = $version
    source = 'https://api.nuget.org/v3/index.json'
    verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
    consumerFrameworks = @('net8.0', 'net10.0')
    result = 'passed'
    consumerPath = $consumer
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo "artifacts/public-release-verification-$Version.json")
Write-Output 'Published NuGet packages restored, built and ran successfully on .NET 8 and .NET 10.'
