using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ClassDescriber
{
    internal static class ClassDescriptor
    {
        public static string Describe(INamedTypeSymbol type)
        {
            var sb = new StringBuilder();

            var ns = type.ContainingNamespace != null ? type.ContainingNamespace.ToDisplayString() : string.Empty;
            sb.AppendLine(type.Name + "  (" + ns + ")");
            sb.AppendLine(new string('─', 42));

            // Modifiers
            var mods = string.Join(" ", new[]
            {
                ToLowerAcc(type.DeclaredAccessibility),
                type.IsStatic ? "static" : null,
                (type.IsAbstract && !type.IsSealed) ? "abstract" : null,
                (type.IsSealed && !type.IsAbstract) ? "sealed" : null
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
            sb.AppendLine("Type: " + mods + " class");

            // Base type / interfaces
            if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
            {
                sb.AppendLine("Inherits: " + type.BaseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }

            var ifaces = type.AllInterfaces
                .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                .ToList();
            if (ifaces.Any())
            {
                sb.AppendLine("Implements: " + string.Join(", ", ifaces));
            }

            // Attributes (class-level)
            var attrs = type.GetAttributes()
                            .Select(a => a.AttributeClass != null ? a.AttributeClass.Name.Replace("Attribute", "") : null)
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .ToList();
            if (attrs.Any())
            {
                sb.AppendLine("Attributes: [" + string.Join(", ", attrs) + "]");
            }

            // Public API (flat)
            var props = type.GetMembers().OfType<IPropertySymbol>()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public).ToList();
            var methods = type.GetMembers().OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public).ToList();
            var fields = type.GetMembers().OfType<IFieldSymbol>()
                .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsConst).ToList();
            var consts = type.GetMembers().OfType<IFieldSymbol>()
                .Where(m => m.IsConst && m.DeclaredAccessibility == Accessibility.Public).ToList();

            if (props.Any())
            {
                sb.AppendLine("Properties (" + props.Count + "): " +
                    string.Join(", ", props.Select(p => Short(p.Type) + " " + p.Name)));
            }

            if (methods.Any())
            {
                sb.AppendLine("Methods (" + methods.Count + "): " +
                    string.Join(", ", methods.Select(Signature)));
            }

            if (fields.Any())
            {
                sb.AppendLine("Fields (" + fields.Count + "): " +
                    string.Join(", ", fields.Select(f => Short(f.Type) + " " + f.Name)));
            }

            if (consts.Any())
            {
                sb.AppendLine("Constants (" + consts.Count + "): " +
                    string.Join(", ", consts.Select(f => Short(f.Type) + " " + f.Name)));
            }

            // Dependencies (types referenced by public API)
            var deps = methods.SelectMany(m => m.Parameters.Select(p => p.Type))
                              .Concat(props.Select(p => p.Type))
                              .Concat(fields.Select(f => f.Type))
                              .Select(t => t.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                              .Distinct()
                              .OrderBy(n => n)
                              .ToList();
            if (deps.Any())
            {
                sb.AppendLine("Depends on: " + string.Join(", ", deps));
            }

            // XML doc presence
            var xml = type.GetDocumentationCommentXml();
            if (!string.IsNullOrWhiteSpace(xml))
            {
                sb.AppendLine("XML summary: present");
            }

            return sb.ToString();
        }

        private static string ToLowerAcc(Accessibility a)
        {
            return a.ToString().ToLowerInvariant();
        }

        private static string Signature(IMethodSymbol m)
        {
            var ps = string.Join(", ", m.Parameters.Select(p => Short(p.Type) + " " + p.Name));
            return m.Name + "(" + ps + ") : " + Short(m.ReturnType);
        }

        private static string Short(ITypeSymbol t)
        {
            return t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }
}
