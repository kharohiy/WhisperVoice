using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WhisperVoice.Services;

namespace WhisperVoice.ViewModels
{
    /// <summary>
    /// Owns download lifecycle: HTTP fetch, progress tracking, cancellation.
    /// Single Responsibility: knows nothing about UI state or model list display.
    /// </summary>
    public sealed class ModelsDownloadViewModel : INotifyPropertyChanged
    {
        private readonly IModelDownloadService _dlSvc;
        private readonly string _modelsDir;

        private CancellationTokenSource? _cts;

        // ── Status ──────────────────────────────────────────────────────────
        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPC(); OnPC(nameof(HasError)); }
        }
        public bool HasError => !string.IsNullOrEmpty(_statusMessage);

        // ── Commands ────────────────────────────────────────────────────────
        public ICommand DownloadCommand { get; }
        public ICommand CancelCommand   { get; }

        /// <summary>Fired on the UI thread after a .bin download completes. Arg = full path.</summary>
        public event Action<string>? ModelFileDownloaded;

        public ModelsDownloadViewModel(IModelDownloadService dlSvc, string modelsDir)
        {
            _dlSvc     = dlSvc;
            _modelsDir = modelsDir;

            DownloadCommand = new RelayCommand<ModelItemViewModel>(
                execute:    OnDownload,
                canExecute: m => m?.CanDownload == true);

            CancelCommand = new RelayCommand(
                execute:    OnCancel,
                canExecute: () => _cts is not null);
        }

        private async void OnDownload(ModelItemViewModel? item)
        {
            if (item is null || !item.CanDownload) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            item.IsDownloading    = true;
            item.DownloadProgress = -1;
            StatusMessage         = string.Empty;

            Directory.CreateDirectory(_modelsDir);
            string dest = Path.Combine(_modelsDir, item.Id + ".bin");

            // Progress<T> marshals to the UI thread via captured SynchronizationContext.
            var progress = new Progress<double>(p => item.DownloadProgress = p);

            try
            {
                await _dlSvc.DownloadAsync(item.Model.Url, dest, item.Model.Sha256, progress, ct);
                item.IsDownloaded  = true;
                item.IsDownloading = false;
                StatusMessage      = string.Empty;
                ModelFileDownloaded?.Invoke(dest);
            }
            catch (OperationCanceledException)
            {
                item.IsDownloading    = false;
                item.DownloadProgress = 0;
            }
            catch (Exception ex)
            {
                WhisperVoice.DiagnosticLogger.Instance.Error("ModelDownload", ex, "Abrupt network or integrity validation failure during model download setup.");
                item.IsDownloading    = false;
                item.DownloadProgress = 0;
                StatusMessage         = $"Download failed: {ex.Message}";
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void OnCancel() => _cts?.Cancel();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}