$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$') { throw 'Invalid package release version.' }
$packages = Join-Path $repo "artifacts/packages/$version"
$consumer = Join-Path $repo ('artifacts/package-consumer/' + [Guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $packages 'release-manifest.json'
if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
$manifestPackages = @()

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $args" }
}

Push-Location $repo
try {
    foreach ($project in 'ZeroMapper.Abstractions', 'ZeroMapper.Generator', 'ZeroMapper') {
        Invoke-DotNet pack "src/$project/$project.csproj" -c Release -o $packages --nologo --no-restore '-m:1'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $budgets = @{ 'ZeroMapper.Abstractions' = 25KB; 'ZeroMapper.Generator' = 400KB; 'ZeroMapper' = 25KB }
    foreach ($id in $budgets.Keys) {
        $package = Get-Item (Join-Path $packages "$id.$version.nupkg")
        if ($package.Length -gt $budgets[$id]) { throw "$id exceeds its package-size budget: $($package.Length) bytes" }
        Write-Output "$id package size: $($package.Length) bytes"
        $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $entries = @($archive.Entries.FullName)
            if ('README.md' -notin $entries) { throw "$id is missing the package README" }
            $readmeReader = [System.IO.StreamReader]::new($archive.GetEntry('README.md').Open())
            try { $packedReadme = $readmeReader.ReadToEnd() } finally { $readmeReader.Dispose() }
            $guide = Get-Content -LiteralPath (Join-Path $repo 'docs/usage-guide.md') -Raw
            if ($packedReadme -cne $guide) { throw "$id does not contain the current full usage guide" }
            if ($id -eq 'ZeroMapper' -and ($entries | Where-Object { $_ -like 'lib/*' })) { throw 'Meta-package must not ship runtime assemblies' }
            if ($id -eq 'ZeroMapper.Generator') {
                if ('analyzers/dotnet/cs/ZeroMapper.Generator.dll' -notin $entries) { throw 'Generator analyzer asset is missing' }
                if ($entries | Where-Object { $_ -like 'lib/*' -or $_ -like '*Microsoft.CodeAnalysis*.dll' }) { throw 'Generator leaked runtime assets' }
            }
            $nuspec = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' }
            $reader = [System.IO.StreamReader]::new($nuspec.Open())
            try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $packageMetadata = $metadata.SelectSingleNode("//*[local-name()='metadata']")
            if ($packageMetadata.id -ne $id -or $packageMetadata.version -ne $version) { throw "Incorrect identity/version in $id" }
            if ($packageMetadata.license.InnerText -ne 'MIT') { throw "Incorrect license in $id" }
            if ($packageMetadata.repository.url -ne 'https://github.com/Rahul2749/ZeroMapper') { throw "Incorrect repository URL in $id" }
            $dependencies = @($metadata.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { $_.id })
            if ($id -eq 'ZeroMapper') {
                if ($dependencies.Count -ne 2 -or 'ZeroMapper.Abstractions' -notin $dependencies -or 'ZeroMapper.Generator' -notin $dependencies) {
                    throw "Unexpected meta-package dependencies: $dependencies"
                }
            } elseif ($dependencies.Count -ne 0) { throw "$id has unexpected package dependencies: $dependencies" }
        } finally { $archive.Dispose() }
        $manifestPackages += [ordered]@{ id = $id; file = $package.Name; bytes = $package.Length; sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
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
    $quickStart = [regex]::Match($guide, '(?s)<!-- verified-quickstart -->\s*```csharp\s*\r?\n(.*?)\r?\n```')
    if (-not $quickStart.Success) { throw 'The verified quick-start code block is missing from the usage guide.' }
    $quickStart.Groups[1].Value | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs')
    Invoke-DotNet restore (Join-Path $consumer 'Consumer.csproj') --source $packages --no-cache
    Invoke-DotNet build (Join-Path $consumer 'Consumer.csproj') -c Release --no-restore '-m:1'
    foreach ($framework in 'net8.0', 'net10.0') {
        Invoke-DotNet run --project (Join-Path $consumer 'Consumer.csproj') -c Release --framework $framework --no-build --no-restore
        $runtimeGenerator = Join-Path $consumer "bin/Release/$framework/ZeroMapper.Generator.dll"
        if (Test-Path -LiteralPath $runtimeGenerator) { throw "Generator leaked into $framework consumer runtime output" }
    }
    [ordered]@{
        version = $version
        verifiedAtUtc = [DateTime]::UtcNow.ToString('o')
        consumerFrameworks = @('net8.0', 'net10.0')
        packageValidation = 'passed'
        sourceCommit = (& git -C $repo rev-parse HEAD)
        sourceDirty = [bool](& git -C $repo status --porcelain --untracked-files=no)
        packages = @($manifestPackages | Sort-Object { $_.id })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath
    Write-Output "Verified package manifest: $manifestPath"
} finally { Pop-Location }
