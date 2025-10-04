using System;
using System.ComponentModel.Design;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClassDescriber.Vsix;

internal sealed class ExplainThisCommand
{
    public const int CommandId = 0x0100;
    public static readonly Guid CommandSet = new Guid("7cdb1d17-9a7f-4d01-8e4e-0e6c7d6f4c1b");

    private readonly AsyncPackage _package;

    private ExplainThisCommand(AsyncPackage package, OleMenuCommandService commandService)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));

        var menuCommandID = new CommandID(CommandSet, CommandId);
        var menuItem = new OleMenuCommand(ExecuteAsync, menuCommandID);
        menuItem.BeforeQueryStatus += UpdateVisibility;
        commandService.AddCommand(menuItem);
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        // Switch to main thread to add commands
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(commandService);
        _ = new ExplainThisCommand(package, commandService!);
    }

    private void UpdateVisibility(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is OleMenuCommand cmd)
        {
            // Only enable when there is text selection
            cmd.Visible = true;
            cmd.Enabled = SelectionHelpers.HasSelection();
        }
    }

    private async void ExecuteAsync(object sender, EventArgs e)
    {
        try
        {
            string code = await SelectionHelpers.GetSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(code))
            {
                await VsShellUtilities.ShowMessageBoxAsync(_package, "Select some code first.", "Explain with AI", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK);
                return;
            }

            var (file, lang) = await SelectionHelpers.GetFileAndLanguageAsync();

            using var http = new HttpClient();
            var client = new AiClient(http);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            string explanation = await client.ExplainAsync(code, lang, file, cts.Token);

            var window = await _package.ShowToolWindowAsync(typeof(ExplainToolWindow), 0, true, _package.DisposalToken)
                         as ExplainToolWindow;

            window?.Control?.AppendResult(file, explanation);
        }
        catch (Exception ex)
        {
            await VsShellUtilities.ShowMessageBoxAsync(_package, ex.Message, "Explain with AI (Error)", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK);
        }
    }
}
