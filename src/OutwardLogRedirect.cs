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
    public static void AppendLine(string line) => TravelButtonPlugin.LogInfo(line);
    public static void Append(string text) => TravelButtonPlugin.LogInfo(text);
    public static void WriteAllText(string text) => TravelButtonPlugin.LogInfo(text);
}