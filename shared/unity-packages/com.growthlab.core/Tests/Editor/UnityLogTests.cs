using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GrowthLab.Core.Tests
{
    public sealed class UnityLogTests
    {
        private sealed class CapturingHandler : ILogHandler
        {
            public readonly List<(LogType Type, string Text)> Logs = new List<(LogType, string)>();
            public readonly List<Exception> Exceptions = new List<Exception>();

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args) =>
                Logs.Add((logType, args.Length == 0 ? format : string.Format(format, args)));

            public void LogException(Exception exception, UnityEngine.Object context) => Exceptions.Add(exception);
        }

        [Test]
        public void Write_FiltersByMinimumLevel_AndPrefixesTag()
        {
            var handler = new CapturingHandler();
            var log = new UnityLog(LogLevel.Warning, new UnityEngine.Logger(handler));

            log.Info("Net", "hidden");
            log.Warning("Net", "slow");

            Assert.AreEqual(1, handler.Logs.Count);
            Assert.AreEqual(LogType.Warning, handler.Logs[0].Type);
            StringAssert.Contains("[Net] slow", handler.Logs[0].Text);
        }

        [Test]
        public void Error_WithException_LogsBoth()
        {
            var handler = new CapturingHandler();
            var log = new UnityLog(LogLevel.Debug, new UnityEngine.Logger(handler));
            var exception = new InvalidOperationException("x");

            log.Error("Core", "failed", exception);

            Assert.AreEqual(LogType.Error, handler.Logs[0].Type);
            CollectionAssert.AreEqual(new[] { exception }, handler.Exceptions);
        }

        [Test]
        public void NoneLevel_DisablesEverything()
        {
            var handler = new CapturingHandler();
            var log = new UnityLog(LogLevel.None, new UnityEngine.Logger(handler));

            log.Error("Core", "dropped");

            Assert.IsFalse(log.IsEnabled(LogLevel.Error));
            CollectionAssert.IsEmpty(handler.Logs);
        }
    }
}
