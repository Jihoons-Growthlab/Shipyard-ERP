using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.SceneManagement;

namespace GrowthLab.Core.Tests
{
    /// <summary>Loader logic with a fake backend. Real scene loading is covered by the PlayMode tests.</summary>
    public sealed class SceneLoaderTests
    {
        private sealed class FakeOperation : ISceneLoadOperation
        {
            public float Progress { get; set; }

            public bool IsDone { get; set; }
        }

        private sealed class FakeBackend : ISceneLoadBackend
        {
            public readonly List<string> Calls = new List<string>();
            public FakeOperation Current;
            public bool ReturnNull;

            public ISceneLoadOperation Load(string sceneName, LoadSceneMode mode)
            {
                Calls.Add("load:" + sceneName + ":" + mode);
                return ReturnNull ? null : Current = new FakeOperation();
            }

            public ISceneLoadOperation Unload(string sceneName)
            {
                Calls.Add("unload:" + sceneName);
                return Current = new FakeOperation();
            }
        }

        private sealed class ListProgress : IProgress<float>
        {
            public readonly List<float> Values = new List<float>();

            public void Report(float value) => Values.Add(value);
        }

        /// <summary>Each "frame" advances the fake operation by one step; completes at the third frame.</summary>
        private static Func<Task> Frames(FakeBackend backend, Action<int> onFrame = null)
        {
            var frame = 0;
            return () =>
            {
                frame++;
                onFrame?.Invoke(frame);
                var op = backend.Current;
                op.Progress = Math.Min(0.9f, frame * 0.45f);
                op.IsDone = frame >= 3;
                return Task.CompletedTask;
            };
        }

        [Test]
        public void Load_ReportsProgress_PublishesEvents_AndCompletes()
        {
            var backend = new FakeBackend();
            var bus = new EventBus();
            var events = new List<string>();
            bus.Subscribe<SceneLoadStarted>(e => events.Add("started:" + e.SceneName));
            bus.Subscribe<SceneLoadCompleted>(e => events.Add("completed:" + e.SceneName));
            var loader = new SceneLoader(backend, Frames(backend), bus);
            var progress = new ListProgress();

            var task = loader.LoadAsync("Level", LoadSceneMode.Additive, progress);

            Assert.IsTrue(task.IsCompleted);
            task.GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "load:Level:Additive" }, backend.Calls);
            CollectionAssert.AreEqual(new[] { 0f, 0.45f, 0.9f, 1f }, progress.Values);
            CollectionAssert.AreEqual(new[] { "started:Level", "completed:Level" }, events);
            Assert.IsFalse(loader.IsBusy);
        }

        [Test]
        public void CancelledBeforeStart_DoesNotTouchBackend()
        {
            var backend = new FakeBackend();
            var loader = new SceneLoader(backend, Frames(backend));
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Catch<OperationCanceledException>(() =>
                    loader.LoadAsync("Level", cancellationToken: cts.Token).GetAwaiter().GetResult());
            }

            CollectionAssert.IsEmpty(backend.Calls);
        }

        [Test]
        public void CancelledDuringLoad_Throws_AndStaysBusyUntilUnityOperationEnds()
        {
            var backend = new FakeBackend();
            using (var cts = new CancellationTokenSource())
            {
                var loader = new SceneLoader(backend, Frames(backend, frame =>
                {
                    if (frame == 1)
                    {
                        cts.Cancel();
                    }
                }));

                Assert.Catch<OperationCanceledException>(() =>
                    loader.LoadAsync("Level", cancellationToken: cts.Token).GetAwaiter().GetResult());

                Assert.IsTrue(loader.IsBusy);
                Assert.Throws<InvalidOperationException>(() => loader.LoadAsync("Other"));

                backend.Current.IsDone = true;
                Assert.IsFalse(loader.IsBusy);
            }
        }

        [Test]
        public void SecondLoadWhileBusy_Throws()
        {
            var backend = new FakeBackend();
            var gate = new TaskCompletionSource<bool>();
            var loader = new SceneLoader(backend, () => gate.Task);

            loader.LoadAsync("A");

            Assert.IsTrue(loader.IsBusy);
            Assert.Throws<InvalidOperationException>(() => loader.LoadAsync("B"));
        }

        [Test]
        public void UnloadableScene_ThrowsArgumentException()
        {
            var backend = new FakeBackend { ReturnNull = true };
            var loader = new SceneLoader(backend, Frames(backend));

            Assert.Throws<ArgumentException>(() => loader.LoadAsync("Missing").GetAwaiter().GetResult());
            Assert.IsFalse(loader.IsBusy);
        }

        [Test]
        public void Unload_WaitsForOperation()
        {
            var backend = new FakeBackend();
            var loader = new SceneLoader(backend, Frames(backend));

            loader.UnloadAsync("Level").GetAwaiter().GetResult();

            CollectionAssert.AreEqual(new[] { "unload:Level" }, backend.Calls);
            Assert.IsTrue(backend.Current.IsDone);
        }

        [Test]
        public void EmptySceneName_Throws()
        {
            var loader = new SceneLoader(new FakeBackend(), () => Task.CompletedTask);
            Assert.Throws<ArgumentException>(() => loader.LoadAsync("").GetAwaiter().GetResult());
        }
    }
}
