# Binding specification — preview 0.1

This document describes implemented behavior. Future engines must satisfy the same observable rules before claiming compatibility. The initial API is provisional.

## Declaration

`[Mapper]` marks a top-level, non-generic partial class. A mapping is an unimplemented partial method of one of these forms:

```csharp
public partial Destination Map(Source source);
public partial void Update(Source source, Destination destination);
```

Static methods are supported. Source and destination are non-nullable classes, not primitive/string types. A new destination must be concrete with a public parameterless constructor. Existing-object maps do not construct the destination. Generic methods, nested/generic/file-local mapper classes, ref parameters, optional parameters, extension methods, and nullable root signatures are rejected with ZM001. Already implemented partial methods are left alone.

## Ordered binding rules

1. Discover public, instance, non-indexer properties through the inheritance chain. Overrides replace their base declarations; hidden members are ambiguous. Ordinary fields, static properties, and indexers are outside this preview. Required destination fields are rejected for new-object maps.
2. Validate configuration by exact destination name. Unknown, duplicate, empty, conflicting, protected allow-list, and non-writable names are errors. `MapAllow` is only valid on update methods. Updates require at least one allowed member.
3. Exclude protected properties, ignored properties, and non-allowed update properties. A protected attribute follows an override chain. New-object maps may not exclude a required member. Public setters are writable; init setters are writable only during creation. Other read-only properties are outside the writable contract.
4. For each remaining writable destination property in ordinal name order, find exactly one readable public source property of the same name. No case folding, flattening, naming convention, or best-match heuristic is applied. Missing or ambiguous bindings are errors.
5. Reject potentially nullable or oblivious reference values flowing into non-nullable reference members, including matching generic type arguments and array elements. Honor `MaybeNull` on source properties/getter returns and `DisallowNull` on destination properties conservatively. Nullable value conversions follow C# implicit conversion rules.
6. Accept built-in implicit identity, numeric, reference, and nullable conversions. Reject user-defined conversions, boxing, dynamic dispatch, and non-identity reference conversions to generic types or arrays (variance and cross-shape nullable analysis are not yet supported). Distinct enum types are rejected; identical enum types are assignable. Same-type object and collection properties retain reference identity: no recursive traversal or cloning occurs.
7. Guard root arguments against null, then create the destination using an object initializer or update each allowed setter. Assignments run in ordinal destination-name order. Getters are evaluated once per bound member. No reflection, LINQ, closures, or cache lookup appears in generated mapping code.

The generated comments are the initial explain report. Each lists the source and destination property and the rule used. A structured report is future work.

## Diagnostics

| ID | Meaning |
|---|---|
| ZM001 | Unsupported mapper, method, source, or destination shape |
| ZM002 | Writable destination property has no readable source match |
| ZM003 | No supported built-in implicit conversion |
| ZM004 | Unsafe nullable binding |
| ZM005 | Hidden or ambiguous member |
| ZM006 | Invalid ignore/allow configuration |
| ZM007 | Required destination member cannot be assigned |

All diagnostics above are errors. A failing mapping method gets no generated implementation, so C# may also report a missing partial implementation. Member binding messages include `DestinationType.Property`. Recursive member paths are reserved for nested mapping.

## Security and behavioral limits

The allow-list is deliberately required for all existing-object updates. New-object mappings use strict convention binding with explicit protected members. Neither rule substitutes for authorization or validation. A property's declared non-nullability can be violated by caller code; this preview does not add per-member runtime checks. Assignable references remain shared, including mutable collections. User constructors and property accessors can throw or have side effects; an update can be partially applied if a later setter throws.

No generated code recurses, so depth/cycle/collection-size guards are not yet needed or implemented. They must be specified before introducing nested/collection mapping. Runtime engine behavior, extension precedence, constructor selection, and projection restrictions remain open design work.
