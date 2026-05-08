using System.ComponentModel;
using System.Runtime.CompilerServices;
using WhisperVoice.Models;

namespace WhisperVoice.ViewModels
{
    public sealed class ModelItemViewModel : INotifyPropertyChanged
    {
        public WhisperModelInfo Model { get; }

        public string Id => Model.Id;
        public string Name => Model.Name;
        public string SizeText => $"{Model.SizeMb:N0} MB";
        public string RecommendedFor => Model.RecommendedFor;
        public string Notes => Model.Notes;

        private bool _isDownloading;
        public bool IsDownloading
        {
            get => _isDownloading;
            set { _isDownloading = value; OnPC(); OnPC(nameof(CanDownload)); OnPC(nameof(ProgressVisible)); }
        }

        private double _progress; // 0-1, -1=indeterminate
        public double DownloadProgress
        {
            get => _progress;
            set { _progress = value; OnPC(); OnPC(nameof(DownloadProgressPct)); OnPC(nameof(IsIndeterminate)); }
        }
        public double DownloadProgressPct => _progress >= 0 ? _progress * 100.0 : 0;
        public bool IsIndeterminate => _progress < 0 && _isDownloading;
        public bool ProgressVisible => _isDownloading;

        private bool _isDownloaded;
        public bool IsDownloaded
        {
            get => _isDownloaded;
            set { _isDownloaded = value; OnPC(); OnPC(nameof(CanDownload)); }
        }

        public bool CanDownload => !_isDownloading && !_isDownloaded;

        public ModelItemViewModel(WhisperModelInfo model) => Model = model;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPC([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
