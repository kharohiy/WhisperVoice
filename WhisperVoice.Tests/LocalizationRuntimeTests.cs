using System;
using System.Threading;
using System.Windows;
using Xunit;
using FluentAssertions;

namespace WhisperVoice.Tests
{
    // Need to run WPF tests in a single thread apartment, but since this just modifies dictionaries, 
    // it usually works if Application is instantiated. We'll use a standard xUnit fact but ensure Application exists.
    public class LocalizationRuntimeTests : IDisposable
    {
        public LocalizationRuntimeTests()
        {
            // Ensure WPF Application context exists for ResourceDictionary testing
            if (Application.Current == null)
            {
                // To avoid STA thread exceptions in xUnit, we might need a dedicated thread, 
                // but usually just new Application() works if we don't show windows.
                // However, Xunit runs in MTA by default. Let's create the app if needed safely.
                try
                {
                    new Application();
                }
                catch (InvalidOperationException)
                {
                    // If it complains about STA, we will handle it in the test itself.
                }
            }
        }

        private void RunInSTA(Action action)
        {
            var thread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    new Application();
                }
                action();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        [Fact]
        public void ApplyInterfaceLanguage_ShouldSwapHeroButtonResources_ForSupportedLanguages()
        {
            RunInSTA(() =>
            {
                // Test English
                App.ApplyInterfaceLanguage("en");
                Application.Current.Resources["BtnHeroIdle"].Should().Be("Record");
                Application.Current.Resources["BtnHeroRecording"].Should().Be("STOP");
                Application.Current.Resources["BtnHeroProcessing"].Should().Be("Processing...");

                // Test Russian
                App.ApplyInterfaceLanguage("ru");
                Application.Current.Resources["BtnHeroIdle"].Should().Be("Запись");
                Application.Current.Resources["BtnHeroRecording"].Should().Be("СТОП");
                Application.Current.Resources["BtnHeroProcessing"].Should().Be("Обработка...");

                // Test Ukrainian
                App.ApplyInterfaceLanguage("uk");
                Application.Current.Resources["BtnHeroIdle"].Should().Be("Запис");
                Application.Current.Resources["BtnHeroRecording"].Should().Be("СТОП");
                Application.Current.Resources["BtnHeroProcessing"].Should().Be("Обробка...");

                // Test German
                App.ApplyInterfaceLanguage("de");
                Application.Current.Resources["BtnHeroIdle"].Should().Be("Aufnehmen");
                Application.Current.Resources["BtnHeroRecording"].Should().Be("STOPP");
                Application.Current.Resources["BtnHeroProcessing"].Should().Be("Verarbeitung...");
                
                // Fallback test (invalid language should fallback to en)
                App.ApplyInterfaceLanguage("invalid_code");
                Application.Current.Resources["BtnHeroIdle"].Should().Be("Record");
            });
        }

        public void Dispose()
        {
            // Clean up not strictly needed for App Domain as tests finish, but good practice
        }
    }
}
