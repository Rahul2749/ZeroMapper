# ZeroMapper: installation and usage guide

**Version: 1.0.0.** This guide documents the supported flat-mapping API and is embedded as the README in all three NuGet packages.

ZeroMapper generates C# object-mapping code during compilation. You declare a partial mapping method; the compiler generates direct assignments and reports unsupported or unsafe bindings as errors. No runtime mapper registration or reflection is needed.

Version 1.0 supports flat property mapping, strict validation, protected destination members, and explicit update allow-lists. This is the supported 1.x contract; future minor versions can add opt-in features without changing existing bindings. Breaking public API or binding-rule changes require a major version. Security fixes may reject previously accepted unsafe mappings.

## 1. Requirements and packages

Use **C# 12 or later** and enable nullable reference types. The assemblies target .NET Standard 2.0; the packaged quick start is verified on **.NET 8 and .NET 10**. A compatible target framework alone is not sufficient: the compiler must also support the generator and language requirements. The repository's build/test scripts require the .NET 10 SDK.

| Package | Purpose | When to install |
|---|---|---|
| `ZeroMapper` | Convenience package depending on both packages below | Recommended for applications |
| `ZeroMapper.Abstractions` | `[Mapper]`, `[MapProtected]`, `[MapIgnore]`, `[MapAllow]` | Shared model projects or manual package selection |
| `ZeroMapper.Generator` | Compiler-only incremental source generator | Alongside abstractions when selecting packages manually |

The abstractions have no third-party package dependencies. The convenience package has two ZeroMapper dependencies. The generator is distributed as an analyzer asset and is excluded from application runtime output.

### Install the convenience package

After 1.0.0 is published, run this in your application project:

```shell
dotnet add package ZeroMapper --version 1.0.0
```

In Visual Studio's NuGet package manager, search for `ZeroMapper` and select version `1.0.0` after publication. Stable versions do not require **Include prerelease**.

To evaluate a locally built release bundle:

```shell
dotnet add package ZeroMapper --version 1.0.0 --source C:/Github/ZeroMapper/artifacts/packages/1.0.0
```

Replace that path with the location of your verified `.nupkg` files. Keep all three packages in the same local feed directory.

Equivalent project configuration:

```xml
<PropertyGroup>
  <LangVersion>12.0</LangVersion>
  <Nullable>enable</Nullable>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="ZeroMapper" Version="1.0.0" />
</ItemGroup>
```

For manual package selection instead of the convenience package:

```xml
<ItemGroup>
  <PackageReference Include="ZeroMapper.Abstractions" Version="1.0.0" />
  <PackageReference Include="ZeroMapper.Generator" Version="1.0.0"
                    PrivateAssets="all" />
</ItemGroup>
```

`PrivateAssets="all"` keeps your generator reference from being exposed as a dependency of a package you publish. The generator still runs when compiling your project. Installing only abstractions does not generate any methods.

## 2. Complete working example

Create a .NET 8 or .NET 10 console project, install the package, and replace `Program.cs` with the following. This exact example is compiled and executed by the release verification script against the packed packages.

<!-- verified-quickstart -->
```csharp
using System;
using ZeroMapper;

var request = new UserRequest
{
    Name = "Ada",
    Bio = "First programmer",
    IsAdmin = true,
    TenantId = 999
};

var user = UserMapper.Create(request);
Console.WriteLine($"Created: {user.Name}; IsAdmin={user.IsAdmin}; TenantId={user.TenantId}");

UserMapper.Update(new UserRequest { Name = "Grace", Bio = null }, user);
Console.WriteLine($"Updated: {user.Name}; Bio={user.Bio ?? "<null>"}; TenantId={user.TenantId}");

// Verify that request values could not change protected properties.
if (user.Name != "Grace" || user.Bio != null || user.IsAdmin || user.TenantId != 42)
    throw new InvalidOperationException("Mapping result did not match the documented behavior.");

public sealed class UserRequest
{
    public string Name { get; set; } = "";
    public string? Bio { get; set; }
    public bool IsAdmin { get; set; }
    public int TenantId { get; set; }
}

public sealed class User
{
    public required string Name { get; set; }
    public string? Bio { get; set; }
    [MapProtected] public bool IsAdmin { get; set; }
    [MapProtected] public int TenantId { get; set; } = 42;
}

[Mapper]
public static partial class UserMapper
{
    public static partial User Create(UserRequest source);

    [MapAllow(nameof(User.Name))]
    [MapAllow(nameof(User.Bio))]
    public static partial void Update(UserRequest source, User destination);
}
```

Expected output:

```text
Created: Ada; IsAdmin=False; TenantId=42
Updated: Grace; Bio=<null>; TenantId=42
```

`Create` allocates a new `User` and maps `Name` and `Bio`. The destination initializer sets `TenantId` to 42. Protected properties retain their constructor/initializer values even when the source has matching properties.

`Update` modifies the same destination instance and writes only `Name` and `Bio`. A null `Bio` overwrites the old value because null is valid for `string?`. This is not a patch operation that skips nulls or distinguishes missing request fields from explicit nulls.

## 3. Declaring mappings

A mapper must be a top-level, non-generic `partial class` marked with `[Mapper]`. Static and instance methods are supported. Declare mapping methods as partial definitions with a semicolon and no body:

- New object: `public static partial Destination Map(Source source);`
- Existing object: `public static partial void Update(Source source, Destination destination);`

Method names are your choice. These signatures describe the two supported shapes; replace `Source` and `Destination` with your actual types. Both root types must be non-nullable classes. For new objects, the destination must be concrete and have a public parameterless constructor. Use public readable source properties and public writable destination properties.

For an instance mapper, omit `static` from both class and methods, instantiate it with `new`, and call the methods normally. No built-in DI registration, global mapper, configuration registry, or service-provider lookup is involved.

Nested mapper classes, generic mappers/methods, nullable root signatures, ref/out parameters, optional parameters, and extension mapping methods are unsupported. Positional records requiring constructor arguments are unsupported. Record classes with a public parameterless constructor and writable/init properties can be used as destinations.

## 4. Exact matching and explicit ignores

Names match exactly and case-sensitively. `Name` does not match `name`, `DisplayName`, or `user_name`. Every writable, non-excluded destination property must have a supported source match. Extra source properties do not cause an error.

Use a per-method ignore for a destination property that should keep its initializer:

```csharp
public sealed class OrderRequest
{
    public int Id { get; set; }
}

public sealed class OrderDto
{
    public long Id { get; init; }
    public string CreatedBy { get; set; } = "system";
}

[ZeroMapper.Mapper]
public static partial class OrderMapper
{
    [ZeroMapper.MapIgnore(nameof(OrderDto.CreatedBy))]
    public static partial OrderDto Create(OrderRequest source);
}
```

`Id` maps using the implicit `int` to `long` conversion. `CreatedBy` keeps `"system"`. Ignore names must identify real destination properties writable in that mapping. Typos and duplicate declarations are errors; `nameof` is recommended. A required property cannot be ignored on a new-object map.

## 5. Protected members and safe updates

`[MapProtected]` belongs on a **destination property**, not on a mapper method or request property. It prevents ZeroMapper assignments in both create and update mappings, including when inherited through a property override. It does not prevent ordinary application code from assigning the property.

`[MapAllow]` belongs on an **update method** and names a destination property. At least one allow-list entry is mandatory. Properties outside that list retain their current values. Adding a matching property to a request DTO therefore does not automatically expand the update.

An update cannot allow-list a protected member or init-only property. Allowing and ignoring the same property is an error. `[MapAllow]` is invalid on new-object mapping methods. A required property is still subject to these rules; on existing objects it can remain unchanged if not allowed.

Use protected members for values such as authorization flags and tenant identifiers, and assign authoritative values in application code. Mapping is not authorization. Validate the request and confirm the caller's right to modify the destination before invoking an update.

Assignments execute in ordinal destination-name order. If a later setter throws, earlier assignments may already have happened. Use application-level transaction/validation logic when atomic behavior is required.

## 6. Nulls, conversions, and object identity

| Binding | Version 1.0 behavior |
|---|---|
| `int` to `long` | Supported built-in implicit numeric conversion |
| `int` to `int?` | Supported |
| `int?` to `int` | Rejected; choose an explicit application policy |
| `string` to `string?` | Supported |
| `string?` to `string` | ZM004; no automatic default or null-forgiving conversion |
| Same enum type to itself | Supported |
| Different enum types | Rejected |
| Compatible same-type object/list/array property | Shallow assignment; reference is shared |
| `Address` to a different `AddressDto` type | Unsupported nested conversion |
| `List<T>` to a different collection shape | Unsupported in version 1.0 |
| Value type to `object` | Boxing conversion rejected |
| User-defined conversion operator | Rejected |

Passing a null root source or destination throws `ArgumentNullException` with the parameter name. Generated methods do not silently return null for null input.

Enable nullable annotations on both model projects. Nullable or oblivious reference values are rejected when they would flow into non-nullable destination members, including matching generic type arguments, enclosing generic types and array elements. Invariant shared generic types must preserve nullable contracts in both directions: for example, `List<string>` to `List<string?>` is rejected because the destination could otherwise insert nulls into the source list. Covariant read-only types such as `IEnumerable<string>` can relax to `IEnumerable<string?>`. `MaybeNull` source annotations and `DisallowNull` destination annotations are handled conservatively. Runtime code can still violate declared annotations; per-member runtime null validation is not added automatically.

Reference assignments preserve identity. If `source.Tags` is a list and the matching `destination.Tags` is assigned directly, changing that list through either reference is visible through the other. A new destination object does not imply a new object graph. Collection element conversion, deep copies, flattening, recursion handling and cycle detection are not implemented.

## 7. Required, init, inherited, and read-only members

Public `init` properties and `required` properties can be initialized in new-object maps. Required properties must be mapped and writable; protected or ignored required properties cause ZM007. Required fields are unsupported.

Existing-object methods write ordinary public setters only. Init-only properties cannot be allow-listed. Read-only properties are not part of the writable destination contract. Ordinary fields, static properties, and indexers are not mapped.

Public inherited properties are considered. An override replaces its base declaration. A member hidden with `new`, including a field or inaccessible member hiding a source property, is rejected when its name would be used for binding. The generator does not silently choose among ambiguous members.

## 8. Diagnostics and troubleshooting

All ZeroMapper diagnostics below are build errors. Fix the reported mapping first; C# may also report a missing partial-method implementation because no implementation is emitted for an invalid map.

| Code | Cause | Typical correction |
|---|---|---|
| ZM001 | Unsupported mapper, method or root type shape | Use a top-level non-generic partial class and a supported method signature; provide a public parameterless destination constructor for creation |
| ZM002 | Writable destination has no readable source match | Align the exact property names, expose a public getter, or explicitly ignore a non-required destination property |
| ZM003 | Unsupported conversion | Use compatible property types or normalize values in application code before mapping |
| ZM004 | Potentially null source into a non-nullable destination | Align nullable contracts or validate/normalize into a non-nullable source model first |
| ZM005 | Hidden or ambiguous member | Remove or rename the hiding member |
| ZM006 | Invalid ignore/allow configuration | Check names, duplicates, conflicts, update-only usage, public setters and protected members |
| ZM007 | Required destination member cannot be assigned | Make the required property publicly writable and mappable, or revise the model contract |

If no method is generated, confirm that both `partial` keywords are present, `[Mapper]` is applied, the generator package is installed, and the build uses C# 12 or later. Do not suppress a missing implementation error by adding an empty method body.

To inspect generated code, add this to the application project and rebuild:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
</PropertyGroup>
```

Inspect the generator's `.g.cs` output under `obj`. Binding comments identify each source and destination member. Generated files are build output; edit the mapping declaration or model, not these files. Leave generated files under `obj` to avoid accidentally compiling them twice.

## 9. Supported scope and performance

This version supports flat create/update mappings, exact names, built-in implicit conversions within the documented limits, nullable diagnostics, required/init properties, protected members, and explicit ignores/allow-lists.

It does **not** include nested or collection element conversion, positional constructor mapping, automatic reverse maps, flattening/unflattening, naming conventions, fluent configuration, custom converters/resolvers, hooks, `ProjectTo`, database-provider integration, runtime/dynamic mapping, or DI helpers. AOT and trimming have not been verified.

Generated mapping bodies contain direct assignments and root argument checks. They do not use reflection, LINQ, boxing, closures, or runtime configuration lookups. For the tested flat mapping fixture, allocations equal the manual implementation's destination allocation; its update fixture allocates zero bytes. Those results are not a guarantee about allocations performed by your own constructors, getters or setters.

The repository includes a BenchmarkDotNet harness against manual mapping. No speed ratio or competitor benchmark result is claimed for version 1.0.

## 10. Repository, license, and feedback

- [Source repository](https://github.com/Rahul2749/ZeroMapper)
- [Issue tracker](https://github.com/Rahul2749/ZeroMapper/issues) for reproducible non-sensitive bugs and feature requests
- [Security policy](https://github.com/Rahul2749/ZeroMapper/blob/main/SECURITY.md) for the configured private reporting route; do not post exploit details publicly

ZeroMapper is MIT licensed and independent of AutoMapper. Mentioning AutoMapper as a related mapping library does not imply affiliation or compatibility with its API.
