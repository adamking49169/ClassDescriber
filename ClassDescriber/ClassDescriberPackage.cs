using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace ClassDescriber
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(ClassDescriberPackage.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ClassDescriberToolWindow))]
    public sealed class ClassDescriberPackage : AsyncPackage
    {
        public const string PackageGuidString = "8e973459-d5f5-459a-9f2a-c0651debd531";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await DescribeClassCommand.InitializeAsync(this);
            await ClassDescriberToolWindowCommand.InitializeAsync(this);
        }

        internal async Task<ClassDescriberToolWindowControl> ShowToolWindowAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();

            var window = await ShowToolWindowAsync(typeof(ClassDescriberToolWindow), 0, true, DisposalToken);
            var control = InitializeToolWindow(window);
            if (window?.Frame is IVsWindowFrame frame)
            {
                // Correct signature: int GetFramePos(VSSETFRAMEPOS[] sfp, out Guid relTo, out int x, out int y, out int cx, out int cy)
                var sfp = new VSSETFRAMEPOS[1];
                Guid relTo;
                int x, y, cx, cy;

                frame.GetFramePos(sfp, out relTo, out x, out y, out cx, out cy);

                // If size looks uninitialized, suggest a compact default (works when floating; VS may ignore when docked).
                if (cx <= 0 || cy <= 0)
                {
                    var flags = VSSETFRAMEPOS.SFP_fMove | VSSETFRAMEPOS.SFP_fSize;
                    var relativeTo = Guid.Empty; // Set relative to "nothing" (screen coords)

                    // Correct signature: int SetFramePos(VSSETFRAMEPOS flags, ref Guid relTo, int x, int y, int cx, int cy)
                    frame.SetFramePos(
                        flags,
                        ref relativeTo,
                        x == 0 ? 200 : x,
                        y == 0 ? 200 : y,
                        420,  // width
                        300   // height
                    );
                }
            }
            return control;
        }
        internal ClassDescriberToolWindowControl InitializeToolWindow(ToolWindowPane window)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (window is ClassDescriberToolWindow typedWindow && typedWindow.Content is ClassDescriberToolWindowControl control)
            {
                control.Initialize(this);
                return control;
            }
            return null;
        }
    }
}
