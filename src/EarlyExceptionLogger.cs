using System;
using System.IO;
using System.Runtime.ExceptionServices;

/// <summary>
/// Early exception logger: registers FirstChance and Unhandled handlers and writes to Desktop files.
/// Designed to be tiny and safe; reference typeof(EarlyExceptionLogger) in Awake() to force registration ASAP.
/// </summary>
public static class EarlyExceptionLogger
{
    static EarlyExceptionLogger()
    {
        try
        {
            // registration confirmation
            try
            {
                string regPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TravelButton_early_registered.txt");
                File.WriteAllText(regPath, $"EarlyExceptionLogger registered at {DateTime.UtcNow:O}\n");
            }
            catch { }

            // FirstChanceException: logs every exception (useful to see what happens right before crash)
            AppDomain.CurrentDomain.FirstChanceException += (sender, args) =>
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TravelButton_firstchance.txt");
                    File.AppendAllText(path, $"[{DateTime.UtcNow:O}] FirstChance: {args.Exception.GetType().FullName}: {args.Exception.Message}\n{args.Exception.StackTrace}\n\n");
                }
                catch { }
            };
        }
        catch { }

        try
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TravelButton_unhandled.txt");
                    File.AppendAllText(path, $"[UnhandledException] {DateTime.UtcNow:O}\n{ex}\n\n");
                }
                catch { }
            };
        }
        catch { }

        try
        {
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, args) =>
            {
                try
                {
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TravelButton_unobservedtask.txt");
                    File.AppendAllText(path, $"[UnobservedTaskException] {DateTime.UtcNow:O}\n{args.Exception}\n\n");
                }
                catch { }
            };
        }
        catch { }
    }
}