using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using ZeroMapper.Generator;

namespace ZeroMapper.Tests;

public sealed class GeneratorTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Append(typeof(MapperAttribute).Assembly.Location).Distinct()
        .Select(p => MetadataReference.CreateFromFile(p)).ToArray();

    private static (Compilation Output, GeneratorDriverRunResult Run) Generate(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);
        var compilation = CSharpCompilation.Create("Test_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText("#nullable enable\nusing ZeroMapper;\n" + source, parseOptions) }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new MapperGenerator().AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (output, driver.GetRunResult());
    }

    private static Assembly Compile(string source)
    {
        var result = Generate(source);
        Assert.Empty(result.Run.Diagnostics);
        Assert.Empty(result.Output.GetDiagnostics().Where(d => d.Severity >= DiagnosticSeverity.Warning));
        using var stream = new MemoryStream();
        var emit = result.Output.Emit(stream);
        Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    [Fact]
    public void Flat_map_handles_required_init_nullable_numeric_and_keyword_properties()
    {
        var assembly = Compile("""
            namespace Example;
            public class Input { public int Id { get; set; } public string Name { get; set; } = "Ada"; public string? Note { get; set; } public int @event { get; set; } }
            public class Output { public long Id { get; init; } public required string Name { get; init; } public string? Note { get; set; } public int @event { get; set; } }
            [Mapper] public static partial class Maps { public static partial Output Map(Input source); }
            public static class Probe {
                public static bool Run() { var result = Maps.Map(new Input { Id = 42, @event = 7 }); return result.Id == 42 && result.Name == "Ada" && result.Note == null && result.@event == 7; }
            }
            """);
        Assert.Equal(true, assembly.GetType("Example.Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void Existing_update_preserves_protected_and_non_allowed_properties()
    {
        var assembly = Compile("""
            public class Input { public string Name { get; set; } = "Changed"; public bool IsAdmin { get; set; } public int TenantId { get; set; } }
            public class Output { public string Name { get; set; } = "Old"; [MapProtected] public bool IsAdmin { get; set; } public int TenantId { get; set; } }
            [Mapper] public partial class Maps { [MapAllow(nameof(Output.Name))] public partial void Update(Input source, Output destination); }
            public static class Probe {
                public static bool Run() { var target = new Output { IsAdmin = true, TenantId = 9 }; new Maps().Update(new Input(), target); return target.Name == "Changed" && target.IsAdmin && target.TenantId == 9; }
            }
            """);
        Assert.Equal(true, assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Theory]
    [InlineData("public int Missing { get; set; }", "", "ZM002")]
    [InlineData("public string Value { get; set; } = \"\";", "public string? Value { get; set; }", "ZM004")]
    [InlineData("public int Value { get; set; }", "public long Value { get; set; }", "ZM003")]
    [InlineData("public required int Value { get; init; }", "", "ZM002")]
    [InlineData("[MapProtected] public required int Value { get; init; }", "public int Value { get; set; }", "ZM007")]
    [InlineData("public required int Value;", "public int Value { get; set; }", "ZM007")]
    [InlineData("public object Value { get; set; } = new();", "public int Value { get; set; }", "ZM003")]
    [InlineData("public string[] Value { get; set; } = [];", "public string?[] Value { get; set; } = [];", "ZM004")]
    [InlineData("public System.Collections.Generic.List<string> Value { get; set; } = [];", "public System.Collections.Generic.List<string?> Value { get; set; } = [];", "ZM004")]
    [InlineData("public System.Collections.Generic.IEnumerable<string> Value { get; set; } = [];", "public System.Collections.Generic.List<string?> Value { get; set; } = [];", "ZM003")]
    [InlineData("public string Value { get; set; } = \"\";", "[System.Diagnostics.CodeAnalysis.MaybeNull] public string Value { get; set; }", "ZM004")]
    [InlineData("[System.Diagnostics.CodeAnalysis.DisallowNull] public string? Value { get; set; }", "public string? Value { get; set; }", "ZM004")]
    [InlineData("public int Value { get; set; }", "public int? Value { get; set; }", "ZM003")]
    [InlineData("public int Value { get; set; }", "public int Value { private get; set; }", "ZM002")]
    [InlineData("public int Value { get; set; }", "public static int Value { get; set; }", "ZM002")]
    [InlineData("public int Value { get; set; }", "public dynamic Value { get; set; } = 1;", "ZM003")]
    public void Unsafe_or_missing_bindings_fail(string target, string source, string id)
    {
        var result = Generate($$"""
            public class Input { {{source}} }
            public class Output { {{target}} }
            [Mapper] public static partial class Maps { public static partial Output Map(Input source); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == id && d.GetMessage().Contains("Output."));
        Assert.DoesNotContain(result.Run.Diagnostics, d => d.Id == "CS8785");
    }

    [Theory]
    [InlineData("", "ZM006")]
    [InlineData("[MapAllow(\"Typo\")]", "ZM006")]
    [InlineData("[MapAllow(\"IsAdmin\")]", "ZM006")]
    [InlineData("[MapAllow(\"Name\"), MapIgnore(\"Name\")]", "ZM006")]
    [InlineData("[MapAllow(\"Name\"), MapAllow(\"Name\")]", "ZM006")]
    [InlineData("[MapAllow(null!)]", "ZM006")]
    public void Invalid_update_configuration_fails_closed(string attributes, string id)
    {
        var result = Generate($$"""
            public class Input { public string Name { get; set; } = ""; public bool IsAdmin { get; set; } }
            public class Output { public string Name { get; set; } = ""; [MapProtected] public bool IsAdmin { get; set; } }
            [Mapper] public partial class Maps { {{attributes}} public partial void Update(Input source, Output target); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == id);
    }

    [Fact]
    public void Protected_attribute_is_inherited_by_overrides()
    {
        var result = Generate("""
            public class Input { public bool IsAdmin { get; set; } }
            public class Base { [MapProtected] public virtual bool IsAdmin { get; set; } }
            public class Output : Base { public override bool IsAdmin { get; set; } }
            [Mapper] public partial class Maps { [MapAllow("IsAdmin")] public partial void Update(Input source, Output target); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM006");
    }

    [Fact]
    public void Ignore_and_protected_members_preserve_initializers_on_new_objects()
    {
        var assembly = Compile("""
            public class Input { public bool IsAdmin { get; set; } = true; }
            public class Output { [MapProtected] public bool IsAdmin { get; set; } public int ServerId { get; set; } = 7; }
            [Mapper] public static partial class Maps { [MapIgnore(nameof(Output.ServerId))] public static partial Output Map(Input source); }
            public static class Probe { public static bool Run() { var value = Maps.Map(new Input()); return !value.IsAdmin && value.ServerId == 7; } }
            """);
        Assert.Equal(true, assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void Null_arguments_throw_with_parameter_name()
    {
        var assembly = Compile("""
            public class Input { public int Id { get; set; } }
            public class Output { public int Id { get; set; } }
            [Mapper] public static partial class Maps {
                public static partial Output Map(Input source);
                [MapAllow("Id")] public static partial void Update(Input input, Output target);
            }
            """);
        var maps = assembly.GetType("Maps")!;
        var exception = Assert.Throws<TargetInvocationException>(() => maps.GetMethod("Map")!.Invoke(null, new object?[] { null }));
        Assert.Equal("source", Assert.IsType<ArgumentNullException>(exception.InnerException).ParamName);
        var input = Activator.CreateInstance(assembly.GetType("Input")!);
        exception = Assert.Throws<TargetInvocationException>(() => maps.GetMethod("Update")!.Invoke(null, new[] { input, null }));
        Assert.Equal("target", Assert.IsType<ArgumentNullException>(exception.InnerException).ParamName);
    }

    [Theory]
    [InlineData("public new int Id { get; set; }")]
    [InlineData("private new int Id { get; set; }")]
    [InlineData("public new int Id;")]
    public void Hidden_source_member_is_rejected(string member)
    {
        var result = Generate($$"""
            public class Base { public int Id { get; set; } }
            public class Input : Base { {{member}} }
            public class Output { public int Id { get; set; } }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM005");
    }

    [Fact]
    public void Inherited_and_overridden_properties_are_bound_once()
    {
        Compile("""
            public class Base { public virtual int Id { get; set; } }
            public class Input : Base { public override int Id { get; set; } }
            public class Output : Base { public override int Id { get; set; } }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
    }

    [Theory]
    [InlineData("[Mapper] public partial class Maps<T> { public partial Output Map(Input source); }")]
    [InlineData("public partial class Outer { [Mapper] public partial class Maps { public partial Output Map(Input source); } }")]
    [InlineData("[Mapper] public partial class Maps { public partial Output Map(ref Input source); }")]
    [InlineData("[Mapper] public partial class Maps { public partial Output Map(Input? source); }")]
    public void Unsupported_signatures_report_a_diagnostic(string mapper)
    {
        var result = Generate("public class Input {} public class Output {} " + mapper);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM001");
    }

    [Fact]
    public void Generated_source_is_deterministic_and_explains_bindings()
    {
        const string source = "public class Input { public int Id { get; set; } } public class Output { public int Id { get; set; } } [Mapper] public partial class Maps { public partial Output Map(Input source); }";
        var first = Generate(source).Run.Results.Single().GeneratedSources.Single();
        var second = Generate(source).Run.Results.Single().GeneratedSources.Single();
        Assert.Equal(first.HintName, second.HintName);
        Assert.Equal(first.SourceText.ToString(), second.SourceText.ToString());
        Assert.Contains("// Bind Output.Id <- Input.Id", first.SourceText.ToString());
        Assert.DoesNotContain("System.Reflection", first.SourceText.ToString());
    }

    [Fact]
    public void Common_path_allocates_only_the_destination_and_updates_allocate_nothing()
    {
        var assembly = Compile("""
            using System;
            public class Input { public int Id { get; set; } public string Name { get; set; } = "Ada"; }
            public class Output { public int Id { get; set; } public string Name { get; set; } = ""; }
            [Mapper] public static partial class Maps {
                public static partial Output Map(Input source);
                [MapAllow("Id"), MapAllow("Name")] public static partial void Update(Input source, Output target);
            }
            public static class Probe {
                private static long Measure(Func<Input, Output> map) {
                    var input = new Input();
                    for (int i = 0; i < 1000; i++) GC.KeepAlive(map(input));
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10000; i++) GC.KeepAlive(map(input));
                    return GC.GetAllocatedBytesForCurrentThread() - before;
                }
                public static long[] Run() {
                    long generated = Measure(Maps.Map);
                    long manual = Measure(static source => new Output { Id = source.Id, Name = source.Name });
                    var input = new Input(); var target = new Output();
                    for (int i = 0; i < 1000; i++) Maps.Update(input, target);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10000; i++) Maps.Update(input, target);
                    long update = GC.GetAllocatedBytesForCurrentThread() - before;
                    return new[] { generated, manual, update };
                }
            }
            """);
        var bytes = (long[])assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null)!;
        Assert.True(bytes[1] > 0);
        Assert.Equal(bytes[1], bytes[0]);
        Assert.Equal(0, bytes[2]);
    }

    [Fact]
    public void Same_type_references_are_shallow_and_enum_and_nullable_value_assignments_work()
    {
        var assembly = Compile("""
            public enum Kind { One, Two }
            public class Input { public int[] Values { get; set; } = [1]; public Kind Kind { get; set; } = Kind.Two; public int Count { get; set; } = 5; }
            public record Output { public int[] Values { get; init; } = []; public Kind Kind { get; init; } public int? Count { get; init; } }
            [Mapper] public static partial class Maps { public static partial Output Map(Input source); }
            public static class Probe { public static bool Run() { var input = new Input(); var output = Maps.Map(input); return object.ReferenceEquals(input.Values, output.Values) && output.Kind == Kind.Two && output.Count == 5; } }
            """);
        Assert.Equal(true, assembly.GetType("Probe")!.GetMethod("Run")!.Invoke(null, null));
    }

    [Fact]
    public void Keyword_namespace_and_identifiers_compile()
    {
        Compile("""
            namespace @event.@namespace;
            public class @class { public int @int { get; set; } }
            public class @struct { public int @int { get; set; } }
            [Mapper] public partial class @void { public partial @struct @return(@class @new); }
            """);
    }

    [Fact]
    public void Multiple_mapper_classes_partial_declarations_and_overloads_compile()
    {
        Compile("""
            public class Input { public int Id { get; set; } }
            public class OtherInput { public int Id { get; set; } }
            public class Output { public int Id { get; set; } }
            [Mapper] public static partial class Maps { public static partial Output Map(Input source); }
            public static partial class Maps { public static partial Output Map(OtherInput source); }
            [Mapper] public partial class OtherMaps { public partial Output Map(Input source); }
            """);
    }

    [Fact]
    public void Closed_generic_root_types_compile()
    {
        Compile("""
            public class Input<T> { public T Value { get; set; } = default!; }
            public class Output<T> { public T Value { get; set; } = default!; }
            [Mapper] public static partial class Maps { public static partial Output<string> Map(Input<string> source); }
            """);
    }

    [Theory]
    [InlineData("public new int Id { get; set; }")]
    [InlineData("private new int Id { get; set; }")]
    [InlineData("public new int Id;")]
    public void Hidden_destination_member_is_rejected(string member)
    {
        var result = Generate($$"""
            public class Input { public int Id { get; set; } }
            public class Base { public int Id { get; set; } }
            public class Output : Base { {{member}} }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM005");
    }

    [Theory]
    [InlineData("public int Id { get; init; }")]
    [InlineData("public int Id { get; private set; }")]
    [InlineData("public int Id => 1;")]
    public void Updates_cannot_allow_members_without_public_mutable_setters(string member)
    {
        var result = Generate($$"""
            public class Input { public int Id { get; set; } }
            public class Output { {{member}} }
            [Mapper] public partial class Maps { [MapAllow("Id")] public partial void Update(Input source, Output target); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM006");
    }

    [Fact]
    public void User_defined_conversions_are_rejected()
    {
        var result = Generate("""
            public class Value { public static implicit operator int(Value value) => 7; }
            public class Input { public Value Id { get; set; } = new(); }
            public class Output { public int Id { get; set; } }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM003");
    }

    [Fact]
    public void Invariant_generic_nullability_cannot_be_relaxed_through_a_shared_reference()
    {
        var result = Generate("""
            public class Input { public System.Collections.Generic.List<string> Values { get; set; } = []; }
            public class Output { public System.Collections.Generic.List<string?> Values { get; set; } = []; }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM004");
    }

    [Fact]
    public void Nullability_in_containing_generic_types_is_checked()
    {
        var result = Generate("""
            public class Outer<T> { public class Inner { public T Value { get; set; } = default!; } }
            public class Input { public Outer<string?>.Inner Value { get; set; } = new(); }
            public class Output { public Outer<string>.Inner Value { get; set; } = new(); }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
        Assert.Contains(result.Run.Diagnostics, d => d.Id == "ZM004");
    }

    [Fact]
    public void Covariant_readonly_generic_nullability_can_be_relaxed()
    {
        Compile("""
            public class Input { public System.Collections.Generic.IEnumerable<string> Values { get; set; } = []; }
            public class Output { public System.Collections.Generic.IEnumerable<string?> Values { get; set; } = []; }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
    }

    [Fact]
    public void Creation_preserves_readonly_and_private_setter_members()
    {
        Compile("""
            public class Input { public int Id { get; set; } }
            public class Output { public int Id { get; init; } public int Private { get; private set; } public string ReadOnly => "server"; }
            [Mapper] public partial class Maps { public partial Output Map(Input source); }
            """);
    }

    [Fact]
    public void Existing_partial_implementations_are_not_replaced()
    {
        Compile("""
            public class Input { }
            public class Output { public int Missing { get; set; } }
            [Mapper] public partial class Maps {
                public partial Output Map(Input source);
                public partial Output Map(Input source) => new Output { Missing = 7 };
            }
            """);
    }
}
