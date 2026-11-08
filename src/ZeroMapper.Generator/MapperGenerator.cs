using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace ZeroMapper.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class MapperGenerator : IIncrementalGenerator
{
    private const string Prefix = "ZeroMapper.";
    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                                  SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor Shape = Rule("ZM001", "Unsupported mapping shape", "{0}");
    private static readonly DiagnosticDescriptor Missing = Rule("ZM002", "Unmapped destination", "Destination '{0}' has no readable source property with the same name");
    private static readonly DiagnosticDescriptor Conversion = Rule("ZM003", "Unsupported conversion", "Destination '{0}' cannot be mapped from '{1}' to '{2}' by a built-in implicit conversion");
    private static readonly DiagnosticDescriptor Nullability = Rule("ZM004", "Unsafe null binding", "Destination '{0}' would accept a possibly null value into a non-nullable member");
    private static readonly DiagnosticDescriptor Ambiguous = Rule("ZM005", "Ambiguous member", "Member '{0}' is hidden or ambiguous; rename it before mapping");
    private static readonly DiagnosticDescriptor Config = Rule("ZM006", "Invalid member configuration", "{0}");
    private static readonly DiagnosticDescriptor Required = Rule("ZM007", "Required member cannot be skipped", "Required destination '{0}' must be mapped and publicly writable");
    private static readonly DiagnosticDescriptor NonWritable = Rule("ZM008", "Matching destination is not publicly writable",
        "Destination '{0}' has a readable source match but no public setter or init accessor; use MapIgnore to skip it explicitly", DiagnosticSeverity.Warning);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analyses = context.SyntaxProvider.ForAttributeWithMetadataName(Prefix + "MapperAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, ct) => Analyze((INamedTypeSymbol)ctx.TargetSymbol, ctx.SemanticModel.Compilation, ct));

        var models = analyses.Select(static (result, _) => result.Model)
            .Where(static model => model != null)
            .Select(static (model, _) => model!)
            .WithTrackingName("MapperModels");
        var sources = models.Select(static (model, ct) => (HintName: HintName(model), Text: Render(model, ct)))
            .WithTrackingName("MapperSource");
        context.RegisterSourceOutput(sources,
            static (ctx, source) => ctx.AddSource(source.HintName, SourceText.From(source.Text, Encoding.UTF8)));

        // Keep source locations on a separate branch so compiler suppression and navigation work
        // without making generated source depend on the identity of a syntax tree.
        var diagnostics = analyses.SelectMany(static (result, _) => result.Diagnostics);
        context.RegisterSourceOutput(diagnostics, static (ctx, diagnostic) => ctx.ReportDiagnostic(diagnostic));
    }

    private static DiagnosticDescriptor Rule(string id, string title, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new DiagnosticDescriptor(id, title, message, "ZeroMapper", severity, true);

    private static (MapperModel? Model, ImmutableArray<Diagnostic> Diagnostics) Analyze(
        INamedTypeSymbol mapper, Compilation compilation, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        void Report(DiagnosticDescriptor rule, ISymbol symbol, params object[] args) =>
            diagnostics.Add(Diagnostic.Create(rule, symbol.Locations.FirstOrDefault(), args));

        if (mapper.ContainingType != null || mapper.Arity != 0 ||
            mapper.DeclaringSyntaxReferences.Any(r => r.GetSyntax(cancellationToken) is not ClassDeclarationSyntax c ||
                !c.Modifiers.Any(SyntaxKind.PartialKeyword) || c.Modifiers.Any(SyntaxKind.FileKeyword)))
        {
            Report(Shape, mapper, "A mapper must be a top-level, non-generic partial class (not file-local).");
            return (null, diagnostics.ToImmutable());
        }

        var methods = ImmutableArray.CreateBuilder<MethodModel>();
        foreach (var method in mapper.GetMembers().OfType<IMethodSymbol>().Where(m => m.IsPartialDefinition))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method.PartialImplementationPart != null) continue;
            var syntax = (MethodDeclarationSyntax)method.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
            bool update = method.ReturnsVoid && method.Parameters.Length == 2;
            if (method.Arity != 0 || method.ReturnsByRef || method.ReturnsByRefReadonly ||
                method.Parameters.Any(p => p.RefKind != RefKind.None || p.IsParams || p.IsOptional || p.IsThis) ||
                (!update && (method.ReturnsVoid || method.Parameters.Length != 1)) ||
                syntax.Modifiers.Any(t => t.IsKind(SyntaxKind.AsyncKeyword) || t.IsKind(SyntaxKind.UnsafeKeyword)))
            {
                Report(Shape, method, "Use partial Destination Map(Source source) or partial void Update(Source source, Destination destination), without generic, ref, optional, or extension parameters.");
                continue;
            }

            var source = method.Parameters[0].Type as INamedTypeSymbol;
            var destination = (update ? method.Parameters[1].Type : method.ReturnType) as INamedTypeSymbol;
            if (source == null || destination == null || source.TypeKind != TypeKind.Class ||
                destination.TypeKind != TypeKind.Class || source.SpecialType != SpecialType.None ||
                destination.SpecialType != SpecialType.None ||
                source.NullableAnnotation == NullableAnnotation.Annotated || destination.NullableAnnotation == NullableAnnotation.Annotated ||
                (!update && (destination.IsAbstract || !destination.InstanceConstructors.Any(c =>
                    c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))))
            {
                Report(Shape, method, "Map non-nullable class types; a new destination requires a public parameterless constructor and must be concrete.");
                continue;
            }

            bool failed = false;
            void Error(DiagnosticDescriptor rule, ISymbol symbol, params object[] args)
            {
                failed = true;
                Report(rule, symbol, args);
            }

            var sourceProperties = Properties(source).ToLookup(p => p.Name, StringComparer.Ordinal);
            var destinationProperties = Properties(destination).ToArray();
            var ignored = Configuration(method, "MapIgnoreAttribute", Error);
            var allowed = Configuration(method, "MapAllowAttribute", Error);
            foreach (var name in ignored.Concat(allowed).Distinct(StringComparer.Ordinal))
            {
                var property = destinationProperties.FirstOrDefault(p => p.Name == name);
                if (property == null)
                    Error(Config, method, $"Configured destination '{destination.Name}.{name}' is not a public instance property.");
                else if (allowed.Contains(name) && !Writable(property, update))
                    Error(Config, method, $"Allow-listed destination '{destination.Name}.{name}' is not publicly writable in this mapping.");
                if (ignored.Contains(name) && allowed.Contains(name))
                    Error(Config, method, $"Destination '{destination.Name}.{name}' is both allowed and ignored.");
                if (property != null && allowed.Contains(name) && Protected(property))
                    Error(Config, method, $"Protected destination '{destination.Name}.{name}' cannot be allow-listed.");
            }
            if (!update && allowed.Count != 0)
                Error(Config, method, "MapAllow is only supported on existing-object update methods.");
            if (update && allowed.Count == 0)
                Error(Config, method, "An existing-object update requires at least one MapAllow destination property.");

            foreach (var field in AllMembers(destination).OfType<IFieldSymbol>().Where(f => f.IsRequired))
                if (!update) Error(Required, method, destination.Name + "." + field.Name);

            var bindings = ImmutableArray.CreateBuilder<string>();
            foreach (var group in destinationProperties.GroupBy(p => p.Name, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = group.First();
                var path = destination.Name + "." + target.Name;
                bool skip = ignored.Contains(target.Name) || Protected(target) || (update && !allowed.Contains(target.Name));
                if (!update && target.IsRequired && (skip || !Writable(target, false)))
                    Error(Required, method, path);
                if (skip) continue;
                var matches = sourceProperties[target.Name].ToArray();
                if (!Writable(target, update))
                {
                    if (!update && !target.IsRequired && group.Count() == 1 && !HiddenByOtherMember(destination, target) &&
                        matches.Length == 1 && matches[0].GetMethod?.DeclaredAccessibility == Accessibility.Public &&
                        !HiddenByOtherMember(source, matches[0]))
                        Report(NonWritable, method, path);
                    continue;
                }
                if (group.Count() != 1) { Error(Ambiguous, method, path); continue; }
                // A hidden non-property member also makes a name unsafe to bind.
                if (HiddenByOtherMember(destination, target)) { Error(Ambiguous, method, path); continue; }
                if (matches.Length > 1 || (matches.Length == 1 && HiddenByOtherMember(source, matches[0])))
                { Error(Ambiguous, method, source.Name + "." + target.Name); continue; }
                if (matches.Length == 0 || matches[0].GetMethod?.DeclaredAccessibility != Accessibility.Public)
                { Error(Missing, method, path); continue; }
                var from = matches[0];
                bool sourceMayBeNull = from.Type.NullableAnnotation != NullableAnnotation.NotAnnotated ||
                    HasAttribute(from.GetAttributes(), "System.Diagnostics.CodeAnalysis.MaybeNullAttribute") ||
                    HasAttribute(from.GetMethod!.GetReturnTypeAttributes(), "System.Diagnostics.CodeAnalysis.MaybeNullAttribute");
                bool targetDisallowsNull = HasAttribute(target.GetAttributes(), "System.Diagnostics.CodeAnalysis.DisallowNullAttribute");
                if (UnsafeNullability(from.Type, target.Type) ||
                    (from.Type.IsReferenceType && sourceMayBeNull &&
                     (target.Type.NullableAnnotation == NullableAnnotation.NotAnnotated || targetDisallowsNull)))
                { Error(Nullability, method, path); continue; }
                var conversion = compilation.ClassifyCommonConversion(from.Type, target.Type);
                if (!conversion.IsImplicit || conversion.IsUserDefined || from.Type.TypeKind == TypeKind.Dynamic ||
                    target.Type.TypeKind == TypeKind.Dynamic || (from.Type.IsValueType && target.Type.IsReferenceType) ||
                    (!conversion.IsIdentity && conversion.IsReference &&
                     (target.Type is IArrayTypeSymbol || target.Type is INamedTypeSymbol { IsGenericType: true })))
                { Error(Conversion, method, path, from.Type.ToDisplayString(), target.Type.ToDisplayString()); continue; }
                bindings.Add(target.Name);
            }
            if (failed) continue;

            methods.Add(new MethodModel(string.Join(" ", syntax.Modifiers.Select(t => t.Text)),
                method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(TypeFormat), method.Name, update,
                source.Name, destination.Name,
                new EquatableArray<ParameterModel>(method.Parameters.Select(p => new ParameterModel(p.Type.ToDisplayString(TypeFormat), p.Name)).ToImmutableArray()),
                new EquatableArray<string>(bindings.ToImmutable())));
        }

        var model = new MapperModel(mapper.ContainingNamespace.IsGlobalNamespace ? "" : mapper.ContainingNamespace.ToDisplayString(),
            mapper.Name, mapper.IsStatic, mapper.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            new EquatableArray<MethodModel>(methods.ToImmutable()));
        return (model, diagnostics.ToImmutable());
    }

    private static string Render(MapperModel model, CancellationToken cancellationToken)
    {
        var code = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (model.Namespace.Length != 0) code.Append("namespace ").Append(model.Namespace).Append(" {\n");
        code.Append(model.IsStatic ? "static " : "").Append("partial class @").Append(model.Name).Append(" {\n");

        foreach (var method in model.Methods.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parameters = method.Parameters.Items;
            code.Append(method.Modifiers).Append(' ').Append(method.ReturnType)
                .Append(" @").Append(method.Name).Append('(');
            code.Append(string.Join(", ", parameters.Select(p => p.Type + " @" + p.Name)));
            code.Append(") {\n");
            foreach (var parameter in parameters)
                code.Append("if (@").Append(parameter.Name).Append(" is null) throw new global::System.ArgumentNullException(nameof(@")
                    .Append(parameter.Name).Append("));\n");
            string sourceName = "@" + parameters[0].Name;
            if (method.Update)
            {
                foreach (var name in method.Bindings.Items)
                    code.Append("// Bind ").Append(method.DestinationName).Append('.').Append(name).Append(" <- ")
                        .Append(method.SourceName).Append('.').Append(name).Append(" (allow-listed)\n")
                        .Append('@').Append(parameters[1].Name).Append(".@").Append(name)
                        .Append(" = ").Append(sourceName).Append(".@").Append(name).Append(";\n");
            }
            else
            {
                code.Append("return new ").Append(method.ReturnType).Append(" {\n");
                foreach (var name in method.Bindings.Items)
                    code.Append("// Bind ").Append(method.DestinationName).Append('.').Append(name).Append(" <- ")
                        .Append(method.SourceName).Append('.').Append(name).Append(" (exact name, implicit conversion)\n")
                        .Append('@').Append(name).Append(" = ").Append(sourceName).Append(".@").Append(name).Append(",\n");
                code.Append("};\n");
            }
            code.Append("}\n");
        }
        code.Append("}\n");
        if (model.Namespace.Length != 0) code.Append("}\n");
        return code.ToString();
    }

    private static string HintName(MapperModel mapper)
    {
        var name = new StringBuilder("ZeroMapper_");
        foreach (var ch in mapper.Name.Take(24))
            name.Append(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ? ch : '_');
        name.Append('_');
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(mapper.FullName));
        for (int i = 0; i < 16; i++) name.Append(hash[i].ToString("x2"));
        return name.Append(".g.cs").ToString();
    }

    private static HashSet<string> Configuration(IMethodSymbol method, string attributeName,
        Action<DiagnosticDescriptor, ISymbol, object[]> error)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in method.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == Prefix + attributeName))
        {
            var name = attribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (string.IsNullOrWhiteSpace(name) || !result.Add(name!))
                error(Config, method, new object[] { "Member configuration must contain non-empty, unique destination names." });
        }
        return result;
    }

    private static bool Writable(IPropertySymbol property, bool update) =>
        property.SetMethod?.DeclaredAccessibility == Accessibility.Public && (!update || !property.SetMethod.IsInitOnly);

    private static bool HasAttribute(IEnumerable<AttributeData> attributes, string name) =>
        attributes.Any(a => a.AttributeClass?.ToDisplayString() == name);

    private static bool Protected(IPropertySymbol property)
    {
        for (IPropertySymbol? current = property; current != null; current = current.OverriddenProperty)
            if (current.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == Prefix + "MapProtectedAttribute")) return true;
        return false;
    }

    private static IEnumerable<ISymbol> AllMembers(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current != null; current = current.BaseType)
            foreach (var member in current.GetMembers()) yield return member;
    }

    private static IEnumerable<IPropertySymbol> Properties(INamedTypeSymbol type)
    {
        var overridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var property in AllMembers(type).OfType<IPropertySymbol>())
        {
            if (overridden.Contains(property)) continue;
            for (var parent = property.OverriddenProperty; parent != null; parent = parent.OverriddenProperty) overridden.Add(parent);
            if (!property.IsStatic && !property.IsIndexer && property.DeclaredAccessibility == Accessibility.Public)
                yield return property;
        }
    }

    private static bool HiddenByOtherMember(INamedTypeSymbol type, IPropertySymbol property)
    {
        for (INamedTypeSymbol? current = type; current != null && !SymbolEqualityComparer.Default.Equals(current, property.ContainingType); current = current.BaseType)
            if (current.GetMembers(property.Name).Length != 0) return true;
        return false;
    }

    private static bool UnsafeNullability(ITypeSymbol source, ITypeSymbol target)
    {
        if (source.IsReferenceType && target.IsReferenceType &&
            source.NullableAnnotation != NullableAnnotation.NotAnnotated && target.NullableAnnotation == NullableAnnotation.NotAnnotated) return true;
        if (source is IArrayTypeSymbol sa && target is IArrayTypeSymbol ta)
            return UnsafeNullability(sa.ElementType, ta.ElementType);
        if (source is INamedTypeSymbol sn && target is INamedTypeSymbol tn &&
            SymbolEqualityComparer.Default.Equals(sn.OriginalDefinition, tn.OriginalDefinition))
        {
            if (sn.ContainingType != null && tn.ContainingType != null &&
                UnsafeNullability(sn.ContainingType, tn.ContainingType)) return true;
            for (int i = 0; i < sn.TypeArguments.Length; i++)
            {
                if (UnsafeNullability(sn.TypeArguments[i], tn.TypeArguments[i])) return true;
                // Invariant shared references must preserve the source's writable contract too.
                if (sn.OriginalDefinition.TypeParameters[i].Variance != VarianceKind.Out &&
                    UnsafeNullability(tn.TypeArguments[i], sn.TypeArguments[i])) return true;
            }
        }
        return false;
    }
}
