using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;

namespace ClassDescriber
{
    public partial class ClassDescriberToolWindowControl : UserControl
    {
        private AsyncPackage package;

        public ClassDescriberToolWindowControl()
        {
            InitializeComponent();
            UpdateStatus();
        }

        internal void Initialize(AsyncPackage owningPackage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package = owningPackage ?? throw new ArgumentNullException(nameof(owningPackage));
        }

        public void SetText(string text)
        {
            SummaryBox.Text = text ?? "";
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            var text = SummaryBox.Text ?? "";
            var lines = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
            TxtStatus.Text = $"{text.Length} chars • {lines} lines";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(SummaryBox.Text ?? ""); } catch { /* ignore */ }
        }

        private async void InsertXml_Click(object sender, RoutedEventArgs e)
        {
            if (package == null)
            {
                return;
            }

            var cancellationToken = GetCancellationToken();
            try
            {
                var inserted = await RoslynActions.TryInsertXmlSummaryForCaretClassAsync(package, cancellationToken);
                if (inserted)
                {
                    await RefreshSummaryAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation triggered by VS shutdown/disposal.
            }
            catch (Exception ex)
            {
                SetText($"Failed to insert summary: {ex.Message}");
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSummaryAsync(GetCancellationToken());
        }

        private void Wrap_Checked(object sender, RoutedEventArgs e)
        {
            SummaryBox.TextWrapping = ChkWrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }

        private void Mono_Checked(object sender, RoutedEventArgs e)
        {
            SummaryBox.FontFamily = (ChkMono.IsChecked == true) ? new System.Windows.Media.FontFamily("Consolas")
                                                                : SystemFonts.MessageFontFamily;
        }

        private CancellationToken GetCancellationToken()
        {
            return package != null ? package.DisposalToken : CancellationToken.None;
        }

        private async Task RefreshSummaryAsync(CancellationToken cancellationToken)
        {
            if (package == null)
            {
                SetText("Package not initialized.");
                return;
            }

            try
            {
                var summary = await RoslynActions.TryDescribeCaretClassAsync(package, cancellationToken);
                SetText(summary ?? "No class found under caret.");
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation triggered by VS shutdown/disposal.
            }
            catch (Exception ex)
            {
                SetText($"Failed to refresh summary: {ex.Message}");
            }
        }
    }
}