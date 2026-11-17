using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace GrowthLab.Core.Tests
{
    public sealed class InitializationPipelineTests
    {
        private List<string> _calls;

        [SetUp]
        public void SetUp() => _calls = new List<string>();

        private Func<CancellationToken, Task> Init(string name) => _ =>
        {
            _calls.Add("init:" + name);
            return Task.CompletedTask;
        };

        private Action Stop(string name) => () => _calls.Add("stop:" + name);

        [Test]
        public void Initialize_RunsInOrder_ShutdownRunsInReverse()
        {
            var pipeline = new InitializationPipeline()
                .Add("a", Init("a"), Stop("a"))
                .Add("b", Init("b"), Stop("b"))
                .Add("c", Init("c"), Stop("c"));

            pipeline.InitializeAsync().GetAwaiter().GetResult();
            Assert.AreEqual(PipelineState.Ready, pipeline.State);
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, pipeline.CompletedSteps);

            pipeline.Shutdown();
            pipeline.Shutdown();

            CollectionAssert.AreEqual(
                new[] { "init:a", "init:b", "init:c", "stop:c", "stop:b", "stop:a" }, _calls);
            Assert.AreEqual(PipelineState.ShutDown, pipeline.State);
        }

        [Test]
        public void FailingStep_RollsBackCompletedSteps_AndRethrows()
        {
            var log = new RecordingLog();
            var pipeline = new InitializationPipeline(log)
                .Add("a", Init("a"), Stop("a"))
                .Add("b", _ => throw new InvalidOperationException("b failed"), Stop("b"))
                .Add("c", Init("c"), Stop("c"));

            Assert.Throws<InvalidOperationException>(() => pipeline.InitializeAsync().GetAwaiter().GetResult());

            CollectionAssert.AreEqual(new[] { "init:a", "stop:a" }, _calls);
            Assert.AreEqual(PipelineState.Failed, pipeline.State);
            Assert.AreEqual(1, log.Count(LogLevel.Error));
        }

        [Test]
        public void FaultedTask_RollsBack()
        {
            var pipeline = new InitializationPipeline()
                .Add("a", Init("a"), Stop("a"))
                .Add("b", _ => Task.FromException(new TimeoutException()), Stop("b"));

            Assert.Throws<TimeoutException>(() => pipeline.InitializeAsync().GetAwaiter().GetResult());
            CollectionAssert.AreEqual(new[] { "init:a", "stop:a" }, _calls);
        }

        [Test]
        public void CancelledBeforeStart_RunsNothing()
        {
            var pipeline = new InitializationPipeline().Add("a", Init("a"), Stop("a"));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Catch<OperationCanceledException>(() =>
                    pipeline.InitializeAsync(cts.Token).GetAwaiter().GetResult());
            }

            CollectionAssert.IsEmpty(_calls);
            Assert.AreEqual(PipelineState.Failed, pipeline.State);
        }

        [Test]
        public void CancelledBetweenSteps_RollsBack()
        {
            using (var cts = new CancellationTokenSource())
            {
                var pipeline = new InitializationPipeline()
                    .Add("a", _ =>
                    {
                        _calls.Add("init:a");
                        cts.Cancel();
                        return Task.CompletedTask;
                    }, Stop("a"))
                    .Add("b", Init("b"), Stop("b"));

                Assert.Catch<OperationCanceledException>(() =>
                    pipeline.InitializeAsync(cts.Token).GetAwaiter().GetResult());
            }

            CollectionAssert.AreEqual(new[] { "init:a", "stop:a" }, _calls);
        }

        [Test]
        public void ShutdownDuringInitialization_CancelsAndRollsBack()
        {
            // Without this, the continuation would be posted to UnitySynchronizationContext and
            // GetResult() on the main thread would deadlock.
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var gate = new TaskCompletionSource<bool>();
                var pipeline = new InitializationPipeline()
                    .Add("a", Init("a"), Stop("a"))
                    .Add("b", _ => gate.Task, Stop("b"))
                    .Add("c", Init("c"), Stop("c"));

                var run = pipeline.InitializeAsync();
                Assert.AreEqual(PipelineState.Initializing, pipeline.State);

                pipeline.Shutdown();
                gate.SetResult(true);

                Assert.IsTrue(run.IsCompleted);
                Assert.Catch<OperationCanceledException>(() => run.GetAwaiter().GetResult());
                CollectionAssert.AreEqual(new[] { "init:a", "stop:b", "stop:a" }, _calls,
                    "b finished initializing, so it is rolled back; c never starts.");
                Assert.AreEqual(PipelineState.Failed, pipeline.State);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }

        [Test]
        public void ShutdownException_IsLogged_AndRemainingStepsStillStop()
        {
            var log = new RecordingLog();
            var pipeline = new InitializationPipeline(log)
                .Add("a", Init("a"), Stop("a"))
                .Add("b", Init("b"), () => throw new InvalidOperationException());

            pipeline.InitializeAsync().GetAwaiter().GetResult();
            pipeline.Shutdown();

            CollectionAssert.AreEqual(new[] { "init:a", "init:b", "stop:a" }, _calls);
            Assert.AreEqual(1, log.Count(LogLevel.Error));
        }

        [Test]
        public void SecondInitialize_Throws_AndAddAfterStart_Throws()
        {
            var pipeline = new InitializationPipeline().Add("a", Init("a"));
            pipeline.InitializeAsync().GetAwaiter().GetResult();

            Assert.Throws<InvalidOperationException>(() => pipeline.InitializeAsync().GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => pipeline.Add("b", Init("b")));
        }

        [Test]
        public void AddUnit_UsesInterfaces()
        {
            var unit = new Unit(_calls);
            var pipeline = new InitializationPipeline().Add("unit", unit);

            pipeline.InitializeAsync().GetAwaiter().GetResult();
            pipeline.Shutdown();

            CollectionAssert.AreEqual(new[] { "init:unit", "stop:unit" }, _calls);
            Assert.Throws<ArgumentException>(() => new InitializationPipeline().Add("x", new object()));
        }

        private sealed class Unit : IInitializable, IShutdownable
        {
            private readonly List<string> _calls;

            public Unit(List<string> calls) => _calls = calls;

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                _calls.Add("init:unit");
                return Task.CompletedTask;
            }

            public void Shutdown() => _calls.Add("stop:unit");
        }
    }
}
