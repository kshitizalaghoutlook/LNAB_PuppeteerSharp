using System;
using System.IO;
using System.Text;

public static class ErrorLogger
{
    private static readonly string LogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ErrorLogs");

    static ErrorLogger()
    {
        Directory.CreateDirectory(LogDirectory);
    }

    public static void Log(Exception ex, string context = "General")
    {
        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            string logFileName = $"{timestamp}_{context}.txt";
            string logFilePath = Path.Combine(LogDirectory, logFileName);

            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {DateTime.Now}");
            sb.AppendLine($"Context: {context}");
            sb.AppendLine($"Exception Type: {ex.GetType().FullName}");
            sb.AppendLine($"Message: {ex.Message}");
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace);

            if (ex.InnerException != null)
            {
                sb.AppendLine("\n--- Inner Exception ---");
                sb.AppendLine($"Exception Type: {ex.InnerException.GetType().FullName}");
                sb.AppendLine($"Message: {ex.InnerException.Message}");
                sb.AppendLine("Stack Trace:");
                sb.AppendLine(ex.InnerException.StackTrace);
            }

            File.WriteAllText(logFilePath, sb.ToString());
            Console.WriteLine($"[ERROR] An error has been logged to: {logFilePath}");
        }
        catch (Exception logEx)
        {
            Console.WriteLine($"[FATAL] Failed to write to error log: {logEx.Message}");
        }
    }
}
