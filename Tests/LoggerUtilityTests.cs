using OllamaToLmStudioProxy;

namespace OllamaToLmStudioProxy.Tests;

public class LoggerUtilityTests
{
    [Fact]
    public void LogMessage_WhenEnabled_WritesToConsole()
    {
        // Arrange
        var enableLogging = true;
        var message = "Test message";

        // Act & Assert - should not throw
        LoggerUtility.LogMessage(enableLogging, message);
    }

    [Fact]
    public void LogMessage_WhenDisabled_DoesNotWriteToConsole()
    {
        // Arrange
        var enableLogging = false;
        var message = "Test message";

        // Act & Assert - should not throw
        LoggerUtility.LogMessage(enableLogging, message);
    }

    [Fact]
    public void LogMessage_WithEmptyMessage_DoesNotThrow()
    {
        LoggerUtility.LogMessage(true, "");
        LoggerUtility.LogMessage(true, string.Empty);
        LoggerUtility.LogMessage(false, "");
    }

    [Fact]
    public void LogMessage_WithNullMessage_DoesNotThrow()
    {
        LoggerUtility.LogMessage(true, null!);
        LoggerUtility.LogMessage(false, null!);
    }

    [Fact]
    public void LogMessage_WithSpecialCharacters_DoesNotThrow()
    {
        LoggerUtility.LogMessage(true, "Test with unicode: 🚀 and symbols: @#$%^&*()");
        LoggerUtility.LogMessage(true, "Line1\nLine2\tTabbed");
    }
}