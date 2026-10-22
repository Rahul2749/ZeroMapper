# ZeroMapper

A strict, source-generated object mapper for .NET. **Version [1.0.0](https://www.nuget.org/packages/ZeroMapper/1.0.0), the first stable release, is available on NuGet.**

This first implementation generates ordinary property assignments, with no runtime reflection or third-party runtime dependencies. It includes `ZeroMapper.Abstractions`, a compiler-only `ZeroMapper.Generator`, and the `ZeroMapper` convenience package. The convenience package depends on the other two packages.

Read the **[complete installation and usage guide](docs/usage-guide.md)** for working examples, safe updates, attributes, conversion rules, diagnostics and troubleshooting. The same full guide is embedded as the NuGet README in every package, and its quick start is verified against the packaged binaries.

## Quick start

Install in a C# 12+ project with nullable reference types enabled:

```powershell
dotnet add package ZeroMapper --version 1.0.0
```

The convenience package includes the abstractions and compiler generator. Alternatively reference the abstractions project and reference the generator project with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`, as in the sample.

See [the 1.0.0 release notes](docs/releases/1.0.0.md) for the release scope and compatibility contract.

```csharp
using ZeroMapper;

public class UserRequest
{
    public string Name { get; set; } = "";
    public bool IsAdmin { get; set; }
}

public class User
{
    public required string Name { get; set; }
    [MapProtected] public bool IsAdmin { get; set; }
}

[Mapper]
public static partial class UserMapper
{
    public static partial User Create(UserRequest source);

    [MapAllow(nameof(User.Name))]
    public static partial void Update(UserRequest source, User destination);
}
```

`Create` matches exact property names and leaves `IsAdmin` at its destination initializer. `Update` only writes `Name`; attempting to allow-list `IsAdmin` is a compile error. Null root arguments throw `ArgumentNullException`. Use `[MapIgnore(nameof(Destination.Property))]` on a mapping method to explicitly exclude another destination property.

Run the example:

```powershell
dotnet run --project samples/ZeroMapper.Sample -c Release
```

## Current scope

- Top-level, non-generic partial mapper classes with static or instance partial methods.
- Class-to-class mapping using a public parameterless destination constructor, public property setters, init setters, and required properties.
- Exact, case-sensitive property matching, including unambiguous inherited properties.
- Built-in implicit conversions, nullable checks, protected properties, explicit ignores, and allow-listed updates.
- Compile errors for missing destination bindings, unsafe nullability, hidden members, unsupported conversions, and invalid configuration.
- Deterministic generated source with comments explaining each binding. Set `EmitCompilerGeneratedFiles=true` to inspect the `.g.cs` files under `obj`.

Assignments of compatible reference properties are **shallow**: the source and destination share that referenced object or collection. This is not deep copying or collection mapping. Nullable checks use compile-time annotations, not runtime validation of dishonest annotations. Property getters, setters and constructors execute user code; updates are not transactional.

Nested conversion, collection element mapping, positional records/constructor binding, enum conversion between distinct types, flattening, fluent configuration, hooks, projections, runtime mapping, and DI integration are not implemented. See [the binding specification](docs/binding-spec.md) for current behavior. Use application-level validation and authorization when mapping untrusted input.

## Benchmarks

```powershell
dotnet run --project benchmarks/ZeroMapper.Benchmarks -c Release -- --filter '*FlatMap*'
```

BenchmarkDotNet compares generated code with equivalent manual assignments and reports allocation. A test checks that the flat generated path allocates the same bytes as manual mapping and that flat updates allocate zero bytes. No performance ratio is claimed yet. Competitor baselines and stable timing gates are pending. Run on a controlled machine before setting timing thresholds.

## Contributing

To prepare a release candidate, run `pwsh ./eng/prepare-release.ps1`. See [the release guide](docs/prerelease.md) for the package manifest, authentication and upload steps. Preparation does not publish packages.

Use .NET 10 SDK; package assemblies target .NET Standard 2.0 and the generator uses Roslyn 4.8. Run `dotnet test ZeroMapper.sln -c Release` and `pwsh ./eng/verify-packages.ps1` before submitting changes. The package script expects dependencies restored first. SourceLink, signing, trusted publishing, and verified trimming/AOT support are release work still pending. All implementation here is original; no competitor source was copied.
