using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace ClassDescriber
{
    /// <summary>
    /// This class implements the tool window exposed by this package and hosts a user control.
    /// </summary>
    /// <remarks>
    /// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
    /// usually implemented by the package implementer.
    /// <para>
    /// This class derives from the ToolWindowPane class provided from the MPF in order to use its
    /// implementation of the IVsUIElementPane interface.
    /// </para>
    /// </remarks>
    [Guid("33060062-da67-4e01-a0db-ae8631577f52")]
    public class ClassDescriberToolWindow : ToolWindowPane
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ClassDescriberToolWindow"/> class.
        /// </summary>
        public ClassDescriberToolWindow() : base(null)
        {
            this.Caption = "ClassDescriberToolWindow";

            // This is the user control hosted by the tool window; Note that, even if this class implements IDisposable,
            // we are not calling Dispose on this object. This is because ToolWindowPane calls Dispose on
            // the object returned by the Content property.
            this.Content = new ClassDescriberToolWindowControl();
        }
    }
}
