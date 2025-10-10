using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClassDescriber
{
    internal static class ClassDescriptor
    {
        public static string Describe(INamedTypeSymbol type)
        {
            var sb = new StringBuilder();

            var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
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

            var props = new List<IPropertySymbol>();
            var methods = new List<IMethodSymbol>();
            var fields = new List<IFieldSymbol>();
            var consts = new List<IFieldSymbol>();

            if (props.Any())
            {
                sb.AppendLine("Properties (" + props.Count + "): " +
                    string.Join(", ", props.Select(p => Short(p.Type) + " " + p.Name)));
            }

            foreach (var member in type.GetMembers())
            {
                switch (member)
                {
                    case IPropertySymbol property when property.DeclaredAccessibility == Accessibility.Public:
                        props.Add(property);
                        break;
                    case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary && method.DeclaredAccessibility == Accessibility.Public:
                        methods.Add(method);
                        break;
                    case IFieldSymbol field when field.DeclaredAccessibility == Accessibility.Public:
                        if (field.IsConst)
                        {
                            consts.Add(field);
                        }
                        else
                        {
                            fields.Add(field);
                        }

                        break;
                }
            }

            if (methods.Any())
            {
                sb.AppendLine("Methods (" + methods.Count + "):");
                for (var i = 0; i < methods.Count; i++)
                {
                    var method = methods[i];
                    sb.AppendLine("  • " + Signature(method));
                    foreach (var detail in DescribeMethodDetails(method))
                    {
                        sb.AppendLine("    " + detail);
                    }

                    if (i < methods.Count - 1)
                    {
                        sb.AppendLine();
                    }
                }
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

            var dependencies = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var method in methods)
            {
                foreach (var parameter in method.Parameters)
                {
                    AddDependency(parameter.Type);
                }
            }

            foreach (var property in props)
            {
                AddDependency(property.Type);
            }

            foreach (var field in fields)
            {
                AddDependency(field.Type);
            }
            if (dependencies.Count > 0)
            {
                sb.AppendLine("Depends on: " + string.Join(", ", dependencies));
            }

            // XML doc presence
            var xml = type.GetDocumentationCommentXml();
            if (!string.IsNullOrWhiteSpace(xml))
            {
                sb.AppendLine("XML summary: present");
            }

            return sb.ToString();

            void AddDependency(ITypeSymbol typeSymbol)
            {
                var typeName = typeSymbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                if (!string.IsNullOrWhiteSpace(typeName))
                {
                    dependencies.Add(typeName);
                }
            }
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

        private static IEnumerable<string> DescribeMethodDetails(IMethodSymbol method)
        {
            yield return method.IsStatic
                ? $"How to call: This is a static method, so call it on the class itself (for example, ClassName.{method.Name}())."
                : $"How to call: This is an instance method, so call it on an object you created (for example, myObject.{method.Name}()).";

            if (method.IsAsync)
            {
                yield return "Behavior: The method is async, which means you usually await it and it won't block other work while it runs.";
            }

            var returnType = Short(method.ReturnType);

            yield return returnType == "void"
                ? "Returns: void (the method does not hand back a value)."
                : $"Returns: {returnType} (the method gives you this type when it finishes).";


            if (method.TypeParameters.Any())
            {
                yield return "Type parameters:";
                foreach (var tp in method.TypeParameters)
                {
                    yield return $"- {tp.Name}: choose the concrete type for this placeholder when you call the method.";
                }
            }

            if (method.Parameters.Any())
            {
                yield return "Parameters:";
                foreach (var parameter in method.Parameters)
                {
                    var typeDisplay = Short(parameter.Type);
                    var descriptionParts = new List<string>();

                    switch (parameter.RefKind)
                    {
                        case RefKind.Ref:
                            descriptionParts.Add("passed by ref");
                            break;
                        case RefKind.Out:
                            descriptionParts.Add("passed as out");
                            break;
                        case RefKind.In:
                            descriptionParts.Add("passed as in");
                            break;
                    }

                    if (parameter.IsParams)
                    {
                        descriptionParts.Add("params array");
                    }

                    if (parameter.HasExplicitDefaultValue)
                    {
                        descriptionParts.Add("optional, default = " + FormatDefaultValue(parameter));
                    }
                    else if (parameter.IsOptional)
                    {
                        descriptionParts.Add("optional");
                    }

                    var extra = descriptionParts.Count > 0
                        ? " (" + string.Join(", ", descriptionParts) + ")"
                        : string.Empty;

                    yield return $"- {parameter.Name} ({typeDisplay}{extra}): provide a value of type {typeDisplay}.";
                }
            }
            else
            {
                yield return "Parameters: none. The method runs without any extra input.";
            }

        }

        private static string FormatDefaultValue(IParameterSymbol parameter)
        {
            if (!parameter.HasExplicitDefaultValue)
            {
                return string.Empty;
            }

            if (parameter.ExplicitDefaultValue == null)
            {
                return "null";
            }

            if (parameter.ExplicitDefaultValue is string s)
            {
                return $"\"{s}\"";
            }

            if (parameter.ExplicitDefaultValue is char c)
            {
                return $"'{c}'";
            }

            if (parameter.ExplicitDefaultValue is bool b)
            {
                return b ? "true" : "false";
            }

            return parameter.ExplicitDefaultValue.ToString() ?? string.Empty;
        }

        private static string Short(ITypeSymbol t)
        {
            return t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
    }
}
