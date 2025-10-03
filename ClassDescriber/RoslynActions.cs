using EnvDTE;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Document = Microsoft.CodeAnalysis.Document;

namespace ClassDescriber
{
    /// <summary>
    /// Helpers that use Roslyn to inspect the current caret location.
    /// </summary>
    internal static class RoslynActions
    {
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

            var descriptorParts = new List<string>();
            descriptorParts.AddRange(GetModifiers(classDeclaration));

            var accessibility = GetAccessibility(symbol.DeclaredAccessibility);
            if (!string.IsNullOrEmpty(accessibility))
            {
                descriptorParts.Add(accessibility);
            }

            descriptorParts.Add(symbol.TypeKind == TypeKind.Class ? "class" : symbol.TypeKind.ToString().ToLowerInvariant());

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
            if (baseType != null && baseType.SpecialType != SpecialType.System_Object)
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

            var members = symbol.GetMembers();
            var methodCount = members.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared);
            var propertyCount = members.OfType<IPropertySymbol>().Count(p => !p.IsImplicitlyDeclared);
            var fieldCount = members.OfType<IFieldSymbol>().Count(f => !f.IsImplicitlyDeclared);
            var eventCount = members.OfType<IEventSymbol>().Count(e => !e.IsImplicitlyDeclared);

            var memberDescriptions = new List<string>();
            if (fieldCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(fieldCount, "field"));
            }

            if (propertyCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(propertyCount, "property"));
            }

            if (methodCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(methodCount, "method"));
            }

            if (eventCount > 0)
            {
                memberDescriptions.Add(CreateCountDescription(eventCount, "event"));
            }

            if (memberDescriptions.Count > 0)
            {
                builder.Append(' ');
                builder.Append("The type defines ");
                builder.Append(JoinWithCommas(memberDescriptions, "and"));
                builder.Append('.');
            }

            return builder.ToString();
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

        private static IEnumerable<string> GetModifiers(ClassDeclarationSyntax classDeclaration)
        {
            foreach (var modifier in classDeclaration.Modifiers)
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

            var workspace = await package.GetServiceAsync(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
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
    }
}