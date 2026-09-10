using System;
using System.IO;

namespace AltTabReplacer.Core.Infrastructure;

/// <summary>
/// 极简日志：写入 %APPDATA%\AltTabReplacer\logs\app-YYYYMMDD.log
/// 不引入 Serilog 之类的依赖，避免给"个人自用工具"加重量。
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string? _logDir;

    private static string LogDir
    {
        get
        {
            if (_logDir != null) return _logDir;
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _logDir = Path.Combine(appData, "AltTabReplacer", "logs");
            Directory.CreateDirectory(_logDir);
            return _logDir;
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        try
        {
            lock (_lock)
            {
                var file = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // 写日志失败就静默：日志不能反过来影响主流程
        }

        // 同时输出到 Debug 输出窗口，方便开发
        System.Diagnostics.Debug.WriteLine(line);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message} :: {ex}");
}
