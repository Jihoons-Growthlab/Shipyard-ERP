using System;
using System.Collections.Generic;

namespace GrowthLab.Core.Tests
{
    internal sealed class RecordingLog : ILog
    {
        public readonly List<(LogLevel Level, string Tag, string Message, Exception Exception)> Entries =
            new List<(LogLevel, string, string, Exception)>();

        public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;

        public bool IsEnabled(LogLevel level) => level >= MinimumLevel && level != LogLevel.None;

        public void Write(LogLevel level, string tag, string message, Exception exception = null)
        {
            if (IsEnabled(level))
            {
                Entries.Add((level, tag, message, exception));
            }
        }

        public int Count(LogLevel level)
        {
            var count = 0;
            foreach (var entry in Entries)
            {
                if (entry.Level == level)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
