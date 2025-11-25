using System;
using System.IO;
using System.Text;

public static class ErrorLogger
{
    private static readonly string ErrorLogDirectory = "ErrorLogs";

    static ErrorLogger()
    {
        try
        {
            if (!Directory.Exists(ErrorLogDirectory))
            {
                Directory.CreateDirectory(ErrorLogDirectory);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL-ERRORLOGGER-INIT] Could not create error log directory '{ErrorLogDirectory}'. Error: {ex.Message}");
        }
    }

    public static void Log(Exception ex, string context = "General Error")
    {
        try
        {
            var timestamp = DateTime.Now;
            var filename = $"error_{timestamp:yyyyMMdd_HHmmss_fff}.txt";
            var filePath = Path.Combine(ErrorLogDirectory, filename);

            var sb = new StringBuilder();
            sb.AppendLine($"# Error Report");
            sb.AppendLine($"========================================");
            sb.AppendLine($"Timestamp: {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Context: {context}");
            sb.AppendLine($"========================================");
            sb.AppendLine();

            var currentException = ex;
            int level = 0;
            while (currentException != null)
            {
                if (level > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"--- Inner Exception (Level {level}) ---");
                }
                sb.AppendLine($"Type: {currentException.GetType().FullName}");
                sb.AppendLine($"Message: {currentException.Message}");
                sb.AppendLine($"Source: {currentException.Source}");
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(currentException.StackTrace ?? "  Not available.");

                currentException = currentException.InnerException;
                level++;
            }

            sb.AppendLine();
            sb.AppendLine($"========================================");
            sb.AppendLine($"# End of Report");

            File.WriteAllText(filePath, sb.ToString());
        }
        catch (Exception logEx)
        {
            Console.WriteLine($"[FATAL-ERRORLOGGER-WRITE] Failed to write error log file. Error: {logEx.Message}");
            Console.WriteLine($"[ORIGINAL-ERROR] {ex}");
        }
    }
}
