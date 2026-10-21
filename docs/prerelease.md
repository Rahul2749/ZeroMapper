# Preparing 0.0.1-preview.1

Repository: https://github.com/Rahul2749/ZeroMapper

## Prepare and verify

From the repository root with PowerShell 7 and .NET 10 SDK:

```powershell
./eng/prepare-release.ps1
```

The consumer checks also require the .NET 8 runtime and targeting pack. The script restores dependencies, performs a clean Release build, runs the tests, packages all three projects, checks their metadata/assets and size budgets, and installs them into fresh .NET 8 and .NET 10 consumer projects. It never uploads anything.

Outputs:

- `artifacts/packages/ZeroMapper.Abstractions.0.0.1-preview.1.nupkg`
- `artifacts/packages/ZeroMapper.Generator.0.0.1-preview.1.nupkg`
- `artifacts/packages/ZeroMapper.0.0.1-preview.1.nupkg`
- `artifacts/packages/release-manifest.json` with sizes, SHA-256 hashes, verification time and test-report hash
- `artifacts/test-results/prerelease.trx`

Only use packages whose hashes match the manifest. Rerun preparation after changing source, metadata or documentation. The manifest is deleted when a new preparation starts so a failed run cannot leave an old success marker.

## Before the first upload

1. Commit and push the source to the repository, and confirm its CI passes.
2. Confirm the NuGet account that will own all three IDs and authenticate through NuGet.org. Never commit API keys to the repository.
3. Establish and document the private reporting channel in `SECURITY.md`.
4. Review the release notes and manifest. Confirm the package version has not already been published; published package versions cannot be overwritten.
5. Upload the abstractions and generator packages first, followed by the convenience package. Review package ID, version, license and dependencies on the NuGet upload preview before submitting. These are separate packages, so publication is not an atomic operation.
6. Wait for validation/indexing, then install the convenience package in a clean project using the public feed and repeat the sample. Tag the source commit `v0.0.1-preview.1` and record the released package hashes.

For this initial interactive release, use the [NuGet upload page](https://www.nuget.org/packages/manage/upload) after signing in. A future CI release can use [NuGet trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), configured for the actual account, repository, workflow and optional release environment. Authentication alone does not publish a package.

Current intentional preview limitations: no author signing, SourceLink, AOT/trim verification, runtime engine or deep mapping. Do not advertise those capabilities. NuGet account and publishing setup are external prerequisites, not local test failures.
