using ClassDescriber.Services;
using EnvDTE;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using Document = Microsoft.CodeAnalysis.Document;

namespace ClassDescriber
{
    /// <summary>
    /// Helpers that use Roslyn to inspect the current caret location.
    /// </summary>
    internal static class RoslynActions
    {
        private static readonly HttpClient SharedHttpClient = new HttpClient();
        public static async Task<string> TryDescribeCaretClassAsync(AsyncPackage package, CancellationToken cancellationToken)
        {
            if (package is null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            var context = await TryGetCaretClassContextAsync(package, cancellationToken).ConfigureAwait(false);
            if (context == null)
            {
                return null;
            }

            return await DescribeClassAsync(context.Document, context.ClassDeclaration, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<string> TryDescribeCurrentDocumentAsync(AsyncPackage package, CancellationToken cancellationToken)
        {
            if (package is null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            var editorContext = await EditorContext.GetDocumentAtCaretAsync();
            if (editorContext == null)
            {
                return null;
            }
            var (document, position) = editorContext.Value;

            var documentSummaryTask = DescribeDocumentAsync(document, cancellationToken);
            var aiSummaryTask = TryGenerateAiInsightAsync(document, position, cancellationToken);

            await Task.WhenAll(documentSummaryTask, aiSummaryTask).ConfigureAwait(false);

            var sections = new List<string>();

            var documentSummary = documentSummaryTask.Result;
            if (!string.IsNullOrWhiteSpace(documentSummary))
            {
                sections.Add(documentSummary.Trim());
            }

            var aiSummary = aiSummaryTask.Result;
            if (!string.IsNullOrWhiteSpace(aiSummary))
            {
                sections.Add("AI insight:\n" + aiSummary.Trim());
            }

            return sections.Count == 0 ? null : string.Join("\n\n", sections); 
        }

        public static async Task<bool> TryInsertXmlSummaryForCaretClassAsync(AsyncPackage package, CancellationToken cancellationToken)
        {
            if (package is null)
            {
                throw new ArgumentNullException(nameof(package));
            }

            var summary = await TryDescribeCaretClassAsync(package, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return false;
            }

            var context = await TryGetCaretClassContextAsync(package, cancellationToken).ConfigureAwait(false);
            if (context == null)
            {
                return false;
            }

            var sourceText = await context.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var indentation = GetIndentation(sourceText, context.ClassDeclaration);
            var summaryTrivia = CreateSummaryTrivia(summary, indentation);
            var leadingTrivia = context.ClassDeclaration.GetLeadingTrivia();
            var withoutExistingDoc = RemoveExistingDocumentationComments(leadingTrivia);
            var newLeadingTrivia = summaryTrivia.AddRange(withoutExistingDoc);

            var updatedClass = context.ClassDeclaration.WithLeadingTrivia(newLeadingTrivia);
            var editor = await DocumentEditor.CreateAsync(context.Document, cancellationToken).ConfigureAwait(false);
            editor.ReplaceNode(context.ClassDeclaration, updatedClass);
            var changedDocument = editor.GetChangedDocument();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            return context.Document.Project.Solution.Workspace.TryApplyChanges(changedDocument.Project.Solution);
        }

        private static async Task<string> DescribeDocumentAsync(Document document, CancellationToken cancellationToken)
        {
            if (document == null)
            {
                return null;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) as CSharpSyntaxNode;
            if (root == null)
            {
                return null;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel == null)
            {
                return null;
            }

            var builder = new StringBuilder();
            var fileName = Path.GetFileName(document.FilePath);

            if (!string.IsNullOrWhiteSpace(fileName))
            {
                builder.Append("This file (");
                builder.Append(fileName);
                builder.Append(") ");
            }
            else
            {
                builder.Append("This file ");
            }

            var compilationUnit = root as CompilationUnitSyntax;
            var namespaceNames = GetNamespaceNames(compilationUnit);

            if (namespaceNames.Count == 1)
            {
                builder.Append("belongs to the ");
                builder.Append(namespaceNames[0]);
                builder.Append(" namespace. ");
            }
            else if (namespaceNames.Count > 1)
            {
                builder.Append("contains code in the ");
                builder.Append(JoinWithCommas(namespaceNames, "and"));
                builder.Append(" namespaces. ");
            }
            else
            {
                builder.Append("is in the global namespace. ");
            }

            if (compilationUnit != null)
            {
                var usingCount = compilationUnit.Usings.Count;
                if (usingCount > 0)
                {
                    builder.Append("It references ");
                    builder.Append(CreateCountDescription(usingCount, "using directive"));
                    builder.Append(". ");
                }
            }

            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var typeSymbols = new List<INamedTypeSymbol>();
            var typeDescriptions = new List<string>();

            foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var symbol = semanticModel.GetDeclaredSymbol(declaration, cancellationToken) as INamedTypeSymbol;
                if (symbol == null || !seen.Add(symbol))
                {
                    continue;
                }

                typeSymbols.Add(symbol);
                var description = DescribeNamedType(symbol, declaration);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    typeDescriptions.Add(description);
                }
            }

            if (typeSymbols.Count > 0)
            {
                builder.Append("It defines ");
                builder.Append(CreateCountDescription(typeSymbols.Count, "type"));
                builder.Append(": ");
                builder.Append(JoinWithCommas(typeSymbols.Select(s => s.Name).ToList(), "and"));
                builder.Append(". ");
            }
            else
            {
                builder.Append("It does not declare any named types. ");
            }

            foreach (var description in typeDescriptions)
            {
                builder.Append(description);
                if (!description.EndsWith("."))
                {
                    builder.Append('.');
                }
                builder.Append(' ');
            }

            var topLevelStatementCount = compilationUnit?.Members.OfType<GlobalStatementSyntax>().Count() ?? 0;
            if (topLevelStatementCount > 0)
            {
                builder.Append("It also contains ");
                builder.Append(CreateCountDescription(topLevelStatementCount, "top-level statement"));
                builder.Append('.');
            }

            return builder.ToString().Trim();
        }


        private static async Task<string> DescribeClassAsync(Document document, ClassDeclarationSyntax classDeclaration, CancellationToken cancellationToken)
        {
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel == null)
            {
                return null;
            }

            var symbol = semanticModel.GetDeclaredSymbol(classDeclaration, cancellationToken) as INamedTypeSymbol;
            if (symbol == null)
            {
                return null;
            }
            return DescribeNamedType(symbol, classDeclaration);
        }

        private static string DescribeNamedType(INamedTypeSymbol symbol, BaseTypeDeclarationSyntax declaration)
        {
            if (symbol == null)
            {
                return null;
            }

            var descriptorParts = new List<string>();

            if (declaration != null)
            {
                descriptorParts.AddRange(GetModifiers(declaration.Modifiers));
            }
            else
            {
                if (symbol.IsStatic)
                {
                    descriptorParts.Add("static");
                }

                if (symbol.IsAbstract && !symbol.IsSealed)
                {
                    descriptorParts.Add("abstract");
                }

                if (symbol.IsSealed && !symbol.IsAbstract && symbol.TypeKind == TypeKind.Class)
                {
                    descriptorParts.Add("sealed");
                }
            }

            var accessibility = GetAccessibility(symbol.DeclaredAccessibility);
            if (!string.IsNullOrEmpty(accessibility))
            {
                descriptorParts.Add(accessibility);
            }

            descriptorParts.Add(GetTypeKindDisplayName(symbol));

            var descriptor = string.Join(" ", descriptorParts.Where(p => !string.IsNullOrEmpty(p)));
            if (string.IsNullOrEmpty(descriptor))
            {
                descriptor = "type";
            }

            var builder = new StringBuilder();
            builder.Append(symbol.Name);
            builder.Append(" is ");
            builder.Append(GetIndefiniteArticle(descriptor));
            builder.Append(' ');
            builder.Append(descriptor);

            var namespaceName = symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace
                ? symbol.ContainingNamespace.ToDisplayString()
                : null;
            if (!string.IsNullOrEmpty(namespaceName))
            {
                builder.Append(" in the ");
                builder.Append(namespaceName);
                builder.Append(" namespace");
            }

            builder.Append('.');

            var baseType = symbol.BaseType;
            if (baseType != null && baseType.SpecialType != SpecialType.System_Object && baseType.SpecialType != SpecialType.System_ValueType)
            {
                builder.Append(' ');
                builder.Append("It derives from ");
                builder.Append(baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                builder.Append('.');
            }

            var interfaceNames = symbol.Interfaces
                .Select(i => i.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                .ToList();
            if (interfaceNames.Count > 0)
            {
                builder.Append(' ');
                builder.Append("It implements ");
                builder.Append(JoinWithCommas(interfaceNames, "and"));
                builder.Append('.');
            }

            if (symbol.TypeKind == TypeKind.Enum)
            {
                var enumMembers = symbol.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => !f.IsImplicitlyDeclared)
                    .Select(f => f.Name)
                    .ToList();
                if (enumMembers.Count > 0)
                {
                    builder.Append(' ');
                    builder.Append("The enumeration defines ");
                    builder.Append(JoinWithCommas(enumMembers, "and"));
                    builder.Append('.');
                }

                return builder.ToString();
            }

            var members = symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared).ToList();
            var methodCount = members.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Ordinary);
            var propertyCount = members.OfType<IPropertySymbol>().Count();
            var fieldCount = members.OfType<IFieldSymbol>().Count(f => !f.IsConst);
            var constantCount = members.OfType<IFieldSymbol>().Count(f => f.IsConst);
            var eventCount = members.OfType<IEventSymbol>().Count();

            var memberDescriptions = new List<string>();

            if (propertyCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(propertyCount, "property"));
            }

            if (methodCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(methodCount, "method"));
            }

            if (fieldCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(fieldCount, "field"));
            }

            if (constantCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(constantCount, "constant"));
            }

            if (eventCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(eventCount, "event"));
            }

            if (memberDescriptions.Count > 0)
            {
                builder.Append(' ');
                builder.Append("It exposes ");
                builder.Append(JoinWithCommas(memberDescriptions, "and"));
                builder.Append('.');
            }

            return builder.ToString();
        }

        private static List<string> GetNamespaceNames(CompilationUnitSyntax compilationUnit)
        {
            var names = new List<string>();
            if (compilationUnit == null)
            {
                return names;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ns in compilationUnit.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            {
                var name = ns.Name?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            return names;
        }

        private static string GetTypeKindDisplayName(INamedTypeSymbol symbol)
        {
            if (symbol == null)
            {
                return string.Empty;
            }

            switch (symbol.TypeKind)
            {
                case TypeKind.Class:
                    return symbol.IsRecord ? "record class" : "class";
                case TypeKind.Struct:
                    return symbol.IsRecord ? "record struct" : "struct";
                case TypeKind.Interface:
                    return "interface";
                case TypeKind.Enum:
                    return "enum";
                case TypeKind.Delegate:
                    return "delegate";
                default:
                    return symbol.TypeKind.ToString().ToLowerInvariant();
            }
        }

        private static SyntaxTriviaList CreateSummaryTrivia(string summary, string indentation)
        {
            var escapedSummary = SecurityElement.Escape(summary) ?? string.Empty;
            var normalized = escapedSummary
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var lines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);

            var builder = new StringBuilder();
            builder.Append(indentation);
            builder.Append("/// <summary>");
            builder.Append(Environment.NewLine);

            foreach (var line in lines)
            {
                builder.Append(indentation);
                builder.Append("/// ");
                builder.Append(line);
                builder.Append(Environment.NewLine);
            }

            builder.Append(indentation);
            builder.Append("/// </summary>");
            builder.Append(Environment.NewLine);

            return SyntaxFactory.ParseLeadingTrivia(builder.ToString());
        }

        private static SyntaxTriviaList RemoveExistingDocumentationComments(SyntaxTriviaList triviaList)
        {
            var filtered = new List<SyntaxTrivia>(triviaList.Count);
            for (int i = 0; i < triviaList.Count; i++)
            {
                var trivia = triviaList[i];
                if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                {
                    if (i + 1 < triviaList.Count && triviaList[i + 1].IsKind(SyntaxKind.EndOfLineTrivia))
                    {
                        i++;
                    }

                    continue;
                }

                filtered.Add(trivia);
            }

            while (filtered.Count > 0 && filtered[0].IsKind(SyntaxKind.EndOfLineTrivia))
            {
                filtered.RemoveAt(0);
            }

            return SyntaxFactory.TriviaList(filtered);
        }

        private static string GetIndentation(SourceText text, ClassDeclarationSyntax classDeclaration)
        {
            var line = text.Lines.GetLineFromPosition(classDeclaration.SpanStart);
            var lineText = line.ToString();
            var indentChars = lineText.TakeWhile(char.IsWhiteSpace).ToArray();
            return new string(indentChars);
        }

        private static IEnumerable<string> GetModifiers(SyntaxTokenList modifiers)
        {
            foreach (var modifier in modifiers)
            {
                switch (modifier.Kind())
                {
                    case SyntaxKind.AbstractKeyword:
                        yield return "abstract";
                        break;
                    case SyntaxKind.StaticKeyword:
                        yield return "static";
                        break;
                    case SyntaxKind.SealedKeyword:
                        yield return "sealed";
                        break;
                    case SyntaxKind.PartialKeyword:
                        yield return "partial";
                        break;
                    case SyntaxKind.UnsafeKeyword:
                        yield return "unsafe";
                        break;
                    case SyntaxKind.ReadOnlyKeyword:
                        yield return "readonly";
                        break;
                    case SyntaxKind.RefKeyword:
                        yield return "ref";
                        break;
                }
            }
        }

        private static string CreateCountDescription(int count, string noun)
        {
            return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
        }

        private static string JoinWithCommas(IReadOnlyList<string> items, string conjunction)
        {
            if (items == null || items.Count == 0)
            {
                return string.Empty;
            }

            if (items.Count == 1)
            {
                return items[0];
            }

            if (items.Count == 2)
            {
                return items[0] + " " + conjunction + " " + items[1];
            }

            var builder = new StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(i == items.Count - 1 ? ", " + conjunction + " " : ", ");
                }

                builder.Append(items[i]);
            }

            return builder.ToString();
        }

        private static string GetAccessibility(Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public:
                    return "public";
                case Accessibility.Internal:
                    return "internal";
                case Accessibility.Private:
                    return "private";
                case Accessibility.Protected:
                    return "protected";
                case Accessibility.ProtectedAndInternal:
                    return "protected internal";
                case Accessibility.ProtectedOrInternal:
                    return "private protected";
                default:
                    return string.Empty;
            }
        }

        private static string GetIndefiniteArticle(string descriptor)
        {
            if (string.IsNullOrEmpty(descriptor))
            {
                return "a";
            }

            var firstChar = descriptor[0];
            var lower = char.ToLowerInvariant(firstChar);
            return "aeiou".IndexOf(lower) >= 0 ? "an" : "a";
        }

        private static async Task<CaretClassContext> TryGetCaretClassContextAsync(AsyncPackage package, CancellationToken cancellationToken)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            VisualStudioWorkspace workspace = null;
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (componentModel != null)
            {
                workspace = componentModel.GetService<VisualStudioWorkspace>();
            }
            var dte = await package.GetServiceAsync(typeof(SDTE)) as DTE;
            if (workspace == null || dte == null)
            {
                return null;
            }

            var activeDocument = dte.ActiveDocument;
            if (activeDocument == null)
            {
                return null;
            }

            if (!(activeDocument.Selection is TextSelection selection))
            {
                return null;
            }

            var filePath = activeDocument.FullName;
            if (string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            var documentId = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (documentId == null)
            {
                return null;
            }

            var line = selection.ActivePoint.Line;
            var column = selection.ActivePoint.LineCharOffset;

            return await Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var document = workspace.CurrentSolution.GetDocument(documentId);
                if (document == null)
                {
                    return null;
                }

                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (line < 1 || line > sourceText.Lines.Count)
                {
                    return null;
                }

                var textLine = sourceText.Lines[line - 1];
                var position = Math.Min(textLine.Start + Math.Max(column - 1, 0), textLine.End);

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) as CSharpSyntaxNode;
                if (root == null)
                {
                    return null;
                }

                var token = root.FindToken(position);
                var classDeclaration = token.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                if (classDeclaration == null)
                {
                    return null;
                }

                return new CaretClassContext(document, classDeclaration, position);
            }, cancellationToken).ConfigureAwait(false);
        }

        private sealed class CaretClassContext
        {
            public CaretClassContext(Document document, ClassDeclarationSyntax classDeclaration, int caretPosition)
            {
                Document = document;
                ClassDeclaration = classDeclaration;
                CaretPosition = caretPosition;
            }

            public Document Document { get; }

            public ClassDeclarationSyntax ClassDeclaration { get; }

            public int CaretPosition { get; }
        }
        private static async Task<string> TryGenerateAiInsightAsync(Document document, int caretPosition, CancellationToken cancellationToken)
        {
            try
            {
                var aiClient = new AiClient(SharedHttpClient);
                if (!aiClient.IsConfigured)
                {
                    return null;
                }

                var sourceText = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                var snippet = ExtractRelevantSnippet(sourceText, root, caretPosition);
                if (string.IsNullOrWhiteSpace(snippet))
                {
                    return null;
                }

                const int maxSnippetLength = 6000;
                if (snippet.Length > maxSnippetLength)
                {
                    snippet = snippet.Substring(0, maxSnippetLength) + "\n// ... truncated";
                }

                var language = NormalizeLanguage(document.Project?.Language);
                var filePath = !string.IsNullOrWhiteSpace(document.FilePath)
                    ? Path.GetFileName(document.FilePath)
                    : document.Name ?? "document";

                return await aiClient.ExplainAsync(snippet, language, filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"AI insight unavailable: {ex.Message}";
            }
        }

        private static string ExtractRelevantSnippet(SourceText sourceText, SyntaxNode root, int caretPosition)
        {
            if (sourceText == null)
            {
                return null;
            }

            if (root is CSharpSyntaxNode csharpRoot)
            {
                var position = caretPosition;
                if (position < 0)
                {
                    position = 0;
                }
                else if (position > sourceText.Length)
                {
                    position = sourceText.Length;
                }
                var token = csharpRoot.FindToken(position);
                var member = token.Parent?.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
                if (member != null)
                {
                    return sourceText.ToString(member.Span);
                }
            }

            return sourceText.ToString();
        }

        private static string NormalizeLanguage(string roslynLanguage)
        {
            if (string.IsNullOrWhiteSpace(roslynLanguage))
            {
                return "csharp";
            }

            if (string.Equals(roslynLanguage, LanguageNames.CSharp, StringComparison.OrdinalIgnoreCase))
            {
                return "csharp";
            }

            if (string.Equals(roslynLanguage, LanguageNames.VisualBasic, StringComparison.OrdinalIgnoreCase))
            {
                return "vb";
            }

            if (string.Equals(roslynLanguage, LanguageNames.FSharp, StringComparison.OrdinalIgnoreCase))
            {
                return "fsharp";
            }

            return roslynLanguage.ToLowerInvariant();
        }
    }
}