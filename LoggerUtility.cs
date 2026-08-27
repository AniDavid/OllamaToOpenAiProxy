namespace OllamaToLmStudioProxy
{
    public static class LoggerUtility
    {
        /// <summary>
        /// Logs a message to the console only if logging is enabled.
        /// </summary>
        /// <param name="enableLogging">Flag indicating whether logging should be active.</param>
        /// <param name="message">The message content to log.</param>
        public static void LogMessage(bool enableLogging, string message)
        {
            if (enableLogging)
            {
                Console.WriteLine($"[LOG] {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}");
            }
        }
    }
}