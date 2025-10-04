using System;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClassDescriber.Vsix;

internal static class SelectionHelpers
{
    public static bool HasSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dte = (DTE)Package.GetGlobalService(typeof(DTE));
        var doc = dte?.ActiveDocument;
        var sel = (TextSelection?)doc?.Selection;
        return sel != null && !sel.IsEmpty;
    }

    public static async Task<string> GetSelectedTextAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE)Package.GetGlobalService(typeof(DTE));
        var doc = dte?.ActiveDocument;
        var sel = (TextSelection?)doc?.Selection;
        return sel?.Text ?? string.Empty;
    }

    public static async Task<(string filePath, string language)> GetFileAndLanguageAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = (DTE)Package.GetGlobalService(typeof(DTE));
        var doc = dte?.ActiveDocument;
        string path = doc?.FullName ?? "(unknown)";
        string lang = doc?.Language ?? "csharp";
        return (path, lang.ToLowerInvariant());
    }
}
