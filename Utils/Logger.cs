using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace DeepSeek_v4_for_VisualStudio.Utils
{
    public static class Logger
    {
        private static readonly string LogFilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "DeepSeekVS", "extension.log");

        static Logger()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            }
            catch { /* 忽略目录创建错误 */ }
        }

        public static void Info(string message, [CallerMemberName] string? member = null)
            => Log("INFO", message, member);
        public static void Warn(string message, [CallerMemberName] string? member = null)
            => Log("WARN", message, member);

        public static void Error(string message, Exception? ex = null, [CallerMemberName] string? member = null)
            => Log("ERROR", $"{message} {ex?.Message}", member);

        private static void Log(string level, string message, string? member)
        {
            string log = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{member}] {message}";
            Debug.WriteLine(log);
            try
            {
                File.AppendAllText(LogFilePath, log + Environment.NewLine);
            }
            catch { /* 写入失败不影响主流程 */ }
        }
    }
}