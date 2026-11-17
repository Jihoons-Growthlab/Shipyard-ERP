using System;
using UnityEngine;

namespace GrowthLab.Core
{
    /// <summary>Forwards messages to <see cref="UnityEngine.Debug.unityLogger"/>.</summary>
    public sealed class UnityLog : ILog
    {
        private readonly UnityEngine.ILogger _logger;

        public UnityLog(LogLevel minimumLevel = LogLevel.Info, UnityEngine.ILogger logger = null)
        {
            MinimumLevel = minimumLevel;
            _logger = logger ?? UnityEngine.Debug.unityLogger;
        }

        public LogLevel MinimumLevel { get; set; }

        public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

        public void Write(LogLevel level, string tag, string message, Exception exception = null)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            var text = string.IsNullOrEmpty(tag) ? message : "[" + tag + "] " + message;
            switch (level)
            {
                case LogLevel.Debug:
                case LogLevel.Info:
                    _logger.Log(LogType.Log, text);
                    break;
                case LogLevel.Warning:
                    _logger.Log(LogType.Warning, text);
                    break;
                default:
                    _logger.Log(LogType.Error, text);
                    if (exception != null)
                    {
                        _logger.LogException(exception);
                    }

                    break;
            }
        }
    }
}
