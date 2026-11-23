using System;
using System.IO;

namespace OnixBoost.Services;

public static class Logger
{
    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}";
            File.AppendAllText(AppPaths.LogFile, line);
        }
        catch { /* ignore */ }
    }
}
