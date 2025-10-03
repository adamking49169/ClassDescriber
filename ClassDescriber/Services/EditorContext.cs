using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.ComponentModelHost;

namespace ClassDescriber.Services
{
    internal static class EditorContext
    {
        public static async Task<(Microsoft.CodeAnalysis.Document doc, int position)?> GetDocumentAtCaretAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Get active VS text view
            var txtMgr = await ServiceProvider.GetGlobalServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
            if (txtMgr == null) return null;
            txtMgr.GetActiveView(1, null, out var vsTextView);
            if (vsTextView == null) return null;

            // Convert to WPF view/buffer
            var compModel = await ServiceProvider.GetGlobalServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var adapters = compModel.GetService<IVsEditorAdaptersFactoryService>();
            IWpfTextView wpfView = adapters.GetWpfTextView(vsTextView);
            if (wpfView == null) return null;

            ITextBuffer buffer = wpfView.TextBuffer;
            int caretPos = wpfView.Caret.Position.BufferPosition.Position;

            // Map buffer -> Roslyn document
            var workspace = await ServiceProvider.GetGlobalServiceAsync(typeof(VisualStudioWorkspace)) as VisualStudioWorkspace;
            if (workspace == null) return null;

            var filePath = buffer.GetFilePath();
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            var docId = workspace.CurrentSolution.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
            if (docId == null) return null;

            var doc = workspace.CurrentSolution.GetDocument(docId);
            return (doc, caretPos);
        }

        private static string GetFilePath(this ITextBuffer buffer)
        {
            buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc);
            return doc?.FilePath;
        }
    }
}
