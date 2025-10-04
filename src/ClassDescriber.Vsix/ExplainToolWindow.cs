using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace ClassDescriber.Vsix;

[Guid("4b6a9c77-1f0e-4b2b-9a65-7c9af2e1ae9e")]
public class ExplainToolWindow : ToolWindowPane
{
    public ExplainToolWindowControl? Control { get; private set; }

    public ExplainToolWindow() : base(null)
    {
        Caption = "Class Describer (AI)";
        Content = Control = new ExplainToolWindowControl();
    }
}
