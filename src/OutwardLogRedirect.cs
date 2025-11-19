using System;

/// <summary>
/// Small compatibility redirector: replace direct writes to outward_log.txt
/// with calls to TravelButtonPlugin so logs go to BepInEx.
/// Usage:
///   OutwardLogRedirect.AppendLine("message");
/// or
///   OutwardLogRedirect.Append($"...{value}...");
/// It intentionally does not write any files.
/// </summary>
public static class OutwardLogRedirect
{
    public static void AppendLine(string line) => TBLog.Info(line);
    public static void Append(string text) => TBLog.Info(text);
    public static void WriteAllText(string text) => TBLog.Info(text);
}
