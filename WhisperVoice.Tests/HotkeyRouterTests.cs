using System;
using FluentAssertions;
using Moq;
using WhisperVoice;
using WhisperVoice.Services;
using Xunit;

namespace WhisperVoice.Tests
{
    public class HotkeyRouterTests
    {
        [Fact]
        public void Rebind_RegistersCorrectHotkeys_InToggleMode()
        {
            var providerMock = new Mock<IKeyboardHookProvider>();
            var router = new HotkeyRouter(providerMock.Object);
            var settings = new AppSettings
            {
                IsPushToTalkEnabled = false,
                HotkeyPrimary = "F8",
                HotkeyTranslate = "F9",
                HotkeyPrompt = "F10"
            };

            router.RebindHotkeys(settings);

            providerMock.Verify(p => p.RegisterHotkey("PrimaryMic", "F8", false, It.IsAny<Action>(), It.IsAny<Action>()), Times.Once);
            providerMock.Verify(p => p.RegisterHotkey("PrimaryLoopback", "Ctrl+F8", false, It.IsAny<Action>(), It.IsAny<Action>()), Times.Once);
            providerMock.Verify(p => p.RegisterHotkey("TranslateMic", "F9", false, It.IsAny<Action>(), It.IsAny<Action>()), Times.Once);
            providerMock.Verify(p => p.RegisterHotkey("PromptMic", "F10", false, It.IsAny<Action>(), It.IsAny<Action>()), Times.Once);
        }

        [Fact]
        public void Rebind_RegistersCorrectHotkeys_InPushToTalkMode()
        {
            var providerMock = new Mock<IKeyboardHookProvider>();
            var router = new HotkeyRouter(providerMock.Object);
            var settings = new AppSettings
            {
                IsPushToTalkEnabled = true,
                HotkeyPrimary = "F8",
                HotkeyMenu = "F7"
            };

            router.RebindHotkeys(settings);

            // Menu is always toggle
            providerMock.Verify(p => p.RegisterHotkey("ToggleMenu", "F7", false, It.IsAny<Action>(), It.IsAny<Action>()), Times.Once);
            
            // Primary is PTT
            providerMock.Verify(p => p.RegisterHotkey("PrimaryPTT", "F8", true, It.IsAny<Action>(), It.IsAny<Action>()), Times.Once);
        }
    }
}
