using System;

namespace GrowthLab.Core
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        None = 4,
    }

    /// <summary>
    /// Minimal logging contract. Implementations must be safe to call from any code path
    /// and must not throw.
    /// </summary>
    public interface ILog
    {
        LogLevel MinimumLevel { get; set; }

        bool IsEnabled(LogLevel level);

        void Write(LogLevel level, string tag, string message, Exception exception = null);
    }

    public static class LogExtensions
    {
        public static void Debug(this ILog log, string tag, string message) => log.Write(LogLevel.Debug, tag, message);

        public static void Info(this ILog log, string tag, string message) => log.Write(LogLevel.Info, tag, message);

        public static void Warning(this ILog log, string tag, string message) => log.Write(LogLevel.Warning, tag, message);

        public static void Error(this ILog log, string tag, string message, Exception exception = null) =>
            log.Write(LogLevel.Error, tag, message, exception);
    }

    /// <summary>Discards every message. Default for components created without a log.</summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();

        public LogLevel MinimumLevel
        {
            get => LogLevel.None;
            set { }
        }

        public bool IsEnabled(LogLevel level) => false;

        public void Write(LogLevel level, string tag, string message, Exception exception = null)
        {
        }
    }
}
