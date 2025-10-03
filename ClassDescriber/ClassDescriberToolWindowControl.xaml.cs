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
        private string _pendingSummaryText;
        private bool _hasPendingSummaryText;
        private bool _isLoaded;

        public ClassDescriberToolWindowControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            UpdateStatus();
        }

        internal void Initialize(AsyncPackage owningPackage)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package = owningPackage ?? throw new ArgumentNullException(nameof(owningPackage));
        }

        public void SetText(string text)
        {
            if (!ThreadHelper.CheckAccess())
            {
                ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetText(text);
                });
                return;
            }

            if (SummaryBox == null || !_isLoaded)
            {
                _pendingSummaryText = text;
                _hasPendingSummaryText = true;
                return;
            }

            SummaryBox.Text = text ?? "";
            _pendingSummaryText = null;
            _hasPendingSummaryText = false;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (SummaryBox == null || TxtStatus == null)
            {
                return;
            }

            var text = SummaryBox.Text ?? "";
            var lines = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
            TxtStatus.Text = $"{text.Length} chars • {lines} lines";
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (SummaryBox == null)
            {
                return;
            }

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
            if (SummaryBox == null)
            {
                return;
            }

            SummaryBox.TextWrapping = ChkWrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }

        private void Mono_Checked(object sender, RoutedEventArgs e)
        {
            if (SummaryBox == null)
            {
                return;
            }

            SummaryBox.FontFamily = (ChkMono.IsChecked == true) ? new System.Windows.Media.FontFamily("Consolas")
                                                                : SystemFonts.MessageFontFamily;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = true;

            if (_hasPendingSummaryText)
            {
                var pending = _pendingSummaryText;
                _pendingSummaryText = null;
                _hasPendingSummaryText = false;
                SetText(pending);
            }
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