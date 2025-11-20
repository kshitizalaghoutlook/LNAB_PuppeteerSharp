using System;
using System.Configuration;
using System.IO;

public static class FileLogger
{
    private static readonly string LogFilePathKey = "LogFilePath";
    private static string _logFilePath;

    static FileLogger()
    {
        try
        {
            _logFilePath = ConfigurationManager.AppSettings[LogFilePathKey];
            if (string.IsNullOrEmpty(_logFilePath))
            {
                _logFilePath = "execution_log.txt"; // Fallback value
                Console.WriteLine($"LogFilePath key not found in App.config. Defaulting to '{_logFilePath}'.");
            }
        }
        catch (Exception ex)
        {
            _logFilePath = "execution_log.txt"; // Fallback on any error
            Console.WriteLine($"Error reading log file path from App.config. Defaulting to '{_logFilePath}'. Error: {ex.Message}");
        }
    }

    public static void Log(string message)
    {
        try
        {
            // Also write to console for immediate visibility during debugging
            Console.WriteLine(message);

            string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            File.AppendAllText(_logFilePath, logMessage + Environment.NewLine);
        }
        catch (Exception ex)
        {
            // Write to console if logging fails, so the error isn't silent
            Console.WriteLine($"[FATAL-LOGGING-ERROR] Failed to write to log file '{_logFilePath}'. Error: {ex.Message}");
        }
    }
}
