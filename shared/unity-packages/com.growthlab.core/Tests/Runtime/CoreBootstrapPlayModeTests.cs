using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace GrowthLab.Core.Tests
{
    public sealed class RecordingBootstrap : CoreBootstrap
    {
        public static List<string> Calls = new List<string>();
        public static bool FailInitialization;

        protected override ILog CreateLog() => NullLog.Instance;

        protected override void Configure(CoreContext context)
        {
            context.Pipeline.Add("a", _ =>
            {
                Calls.Add("init:a");
                return FailInitialization ? Task.FromException(new InvalidOperationException("fail")) : Task.CompletedTask;
            }, () => Calls.Add("stop:a"));
        }

        protected override void OnReady(CoreContext context) => Calls.Add("ready");

        protected override void OnInitializationFailed(Exception exception) => Calls.Add("failed:" + exception.Message);
    }

    public sealed class RecordingConsumer : MonoBehaviour, ICoreConsumer
    {
        public CoreContext Received;
        public int ConstructCount;

        /// <summary>Null until Start runs; then whether Construct had already been called.</summary>
        public bool? HadContextAtStart;

        public void Construct(CoreContext context)
        {
            Received = context;
            ConstructCount++;
        }

        private void Start() => HadContextAtStart = Received != null;
    }

    public sealed class ThrowingConsumer : MonoBehaviour, ICoreConsumer
    {
        public void Construct(CoreContext context) => throw new InvalidOperationException("consumer bug");
    }

    public sealed class CoreBootstrapPlayModeTests
    {
        private readonly List<GameObject> _created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            RecordingBootstrap.Calls = new List<string>();
            RecordingBootstrap.FailInitialization = false;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (var go in _created)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _created.Clear();
            yield return null; // let Destroy run so the next test starts without an active bootstrap
            Assert.IsNull(CoreBootstrap.Active);
        }

        private GameObject Create(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go;
        }

        [UnityTest]
        public IEnumerator Initializes_InjectsConsumers_AndShutsDownOnDestroy()
        {
            var root = Create("Bootstrap");
            var bootstrap = root.AddComponent<RecordingBootstrap>();
            var child = Create("Child");
            child.transform.SetParent(root.transform);
            var childConsumer = child.AddComponent<RecordingConsumer>();
            var sceneConsumer = Create("SceneConsumer").AddComponent<RecordingConsumer>();

            yield return null; // Start
            yield return null;

            Assert.AreEqual(PipelineState.Ready, bootstrap.State);
            Assert.AreSame(bootstrap.Context, childConsumer.Received, "Child under the persisted bootstrap.");
            Assert.AreSame(bootstrap.Context, sceneConsumer.Received, "Consumer in the active scene.");
            Assert.AreEqual("DontDestroyOnLoad", root.scene.name);

            Object.Destroy(root);
            yield return null;

            CollectionAssert.AreEqual(new[] { "init:a", "ready", "stop:a" }, RecordingBootstrap.Calls);
        }

        [UnityTest]
        public IEnumerator FailedInitialization_CallsHook_AndDoesNotInject()
        {
            RecordingBootstrap.FailInitialization = true;
            var bootstrap = Create("Bootstrap").AddComponent<RecordingBootstrap>();
            var consumer = Create("Consumer").AddComponent<RecordingConsumer>();

            yield return null;
            yield return null;

            Assert.AreEqual(PipelineState.Failed, bootstrap.State);
            Assert.IsNull(consumer.Received);
            CollectionAssert.AreEqual(new[] { "init:a", "failed:fail" }, RecordingBootstrap.Calls);
        }
        [UnityTest]
        public IEnumerator ThrowingConsumer_DoesNotBlockOtherConsumers_OrReady()
        {
            var bootstrap = Create("Bootstrap").AddComponent<RecordingBootstrap>();
            var target = Create("Consumers");
            target.AddComponent<ThrowingConsumer>();
            var after = target.AddComponent<RecordingConsumer>();

            yield return null;
            yield return null;

            Assert.AreEqual(PipelineState.Ready, bootstrap.State);
            Assert.AreSame(bootstrap.Context, after.Received, "Consumer after the throwing one still injected.");
            CollectionAssert.Contains(RecordingBootstrap.Calls, "ready");
        }

        [UnityTest]
        public IEnumerator SecondBootstrap_RemovesItself_AndCoreInitializesOnce()
        {
            var first = Create("First").AddComponent<RecordingBootstrap>();
            LogAssert.Expect(LogType.Warning, new Regex("already active"));
            var second = Create("Second").AddComponent<RecordingBootstrap>();

            yield return null;
            yield return null;

            Assert.AreSame(first, CoreBootstrap.Active);
            Assert.IsTrue(second == null, "Duplicate component destroyed.");
            CollectionAssert.AreEqual(new[] { "init:a", "ready" }, RecordingBootstrap.Calls);
        }

        [UnityTest]
        public IEnumerator WhenReady_RunsQueuedCallbacks_ThenImmediateOnes()
        {
            var received = new List<CoreContext>();
            CoreBootstrap.WhenReady(received.Add);
            var bootstrap = Create("Bootstrap").AddComponent<RecordingBootstrap>();
            Assert.AreEqual(0, received.Count);

            yield return null;
            yield return null;

            CoreBootstrap.WhenReady(received.Add);
            Assert.AreEqual(2, received.Count);
            Assert.AreSame(bootstrap.Context, received[0]);
            Assert.AreSame(bootstrap.Context, received[1]);
        }

        [UnityTest]
        public IEnumerator ContextInstantiate_InjectsRuntimeObjects()
        {
            var bootstrap = Create("Bootstrap").AddComponent<RecordingBootstrap>();

            yield return null;
            yield return null;

            // Created after the injection pass, so only Instantiate can inject the copy.
            var prefab = Create("Prefab");
            prefab.SetActive(false);
            prefab.AddComponent<RecordingConsumer>();

            var instance = bootstrap.Context.Instantiate(prefab);
            _created.Add(instance);
            var consumer = instance.GetComponent<RecordingConsumer>();
            Assert.AreSame(bootstrap.Context, consumer.Received);
            Assert.AreEqual(1, consumer.ConstructCount);
        }
    }
}
