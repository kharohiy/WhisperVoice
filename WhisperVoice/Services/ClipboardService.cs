using System;
using System.Threading.Tasks;
using System.Windows;
using WindowsInput;
using WindowsInput.Native;

namespace WhisperVoice.Services
{
    public interface IClipboardService
    {
        Task CopyAndPasteAsync(string text, bool injectPaste);
    }

    public class ClipboardService : IClipboardService
    {
        private readonly InputSimulator _inputSim = new();

        public async Task CopyAndPasteAsync(string text, bool injectPaste)
        {
            bool copied = false;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    // Clipboard operations must happen on an STA thread
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        System.Windows.Clipboard.SetText(text);
                    });
                    copied = true;
                    break;
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Instance.Warn("ClipboardService", $"Clipboard lock: {ex.Message}. Retrying...");
                    await Task.Delay(50);
                }
            }

            if (copied && injectPaste)
            {
                await Task.Delay(100);
                _inputSim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);
            }
        }
    }
}
