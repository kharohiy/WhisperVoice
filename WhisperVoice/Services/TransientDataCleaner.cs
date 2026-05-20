using System;
using System.IO;

namespace WhisperVoice.Services
{
    public static class TransientDataCleaner
    {
        public static void Cleanup(string tempWavPath, string tempTxtPath, string modelsDir, Action<string, Exception>? onError = null, Action<string>? onInfo = null)
        {
            try { if (File.Exists(tempWavPath)) File.Delete(tempWavPath); } 
            catch (Exception ex) { onError?.Invoke("Temp WAV cleanup failed", ex); }
            
            try { if (File.Exists(tempTxtPath)) File.Delete(tempTxtPath); } 
            catch (Exception ex) { onError?.Invoke("Temp TXT cleanup failed", ex); }
            
            try 
            { 
                if (Directory.Exists(modelsDir))
                {
                    foreach (var file in Directory.GetFiles(modelsDir, "*.part"))
                    {
                        File.Delete(file);
                        onInfo?.Invoke($"Cleaned up orphaned download: {Path.GetFileName(file)}");
                    }
                }
            } 
            catch (Exception ex) { onError?.Invoke("Orphaned .part models cleanup failed", ex); }
        }
    }
}
