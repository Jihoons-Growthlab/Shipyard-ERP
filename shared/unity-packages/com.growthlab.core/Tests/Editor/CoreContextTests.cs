using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

namespace GrowthLab.Core.Tests
{
    public sealed class CountingConsumer : MonoBehaviour, ICoreConsumer
    {
        public int Calls;
        public CoreContext Received;

        public void Construct(CoreContext context)
        {
            Calls++;
            Received = context;
        }
    }

    public sealed class CoreContextTests
    {
        [Test]
        public void RegistersStandardServices()
        {
            using (var context = new CoreContext(NullLog.Instance))
            {
                Assert.AreSame(context.Log, context.Services.Get<ILog>());
                Assert.AreSame(context.Events, context.Services.Get<IEventBus>());
                Assert.AreSame(context.Settings, context.Services.Get<SettingsStore>());
                Assert.AreSame(context.Scenes, context.Services.Get<ISceneLoader>());
            }
        }

        [Test]
        public void Dispose_ShutsDownPipeline_AndClearsSubscriptions()
        {
            var calls = new List<string>();
            var context = new CoreContext(NullLog.Instance);
            context.Pipeline.Add("a", _ => Task.CompletedTask, () => calls.Add("stop:a"));
            context.Pipeline.InitializeAsync().GetAwaiter().GetResult();
            context.Events.Subscribe<int>(_ => calls.Add("event"));

            context.Dispose();
            context.Dispose();
            context.Events.Publish(1);

            CollectionAssert.AreEqual(new[] { "stop:a" }, calls);
            Assert.AreEqual(PipelineState.ShutDown, context.Pipeline.State);
        }

        [Test]
        public void InjectHierarchy_ReachesInactiveChildren_Once()
        {
            var root = new GameObject("root");
            try
            {
                var child = new GameObject("child");
                child.transform.SetParent(root.transform);
                child.SetActive(false);
                var rootConsumer = root.AddComponent<CountingConsumer>();
                var childConsumer = child.AddComponent<CountingConsumer>();

                using (var context = new CoreContext(NullLog.Instance))
                {
                    Assert.AreEqual(2, CoreInjection.InjectHierarchy(root, context));
                    Assert.AreSame(context, rootConsumer.Received);
                    Assert.AreSame(context, childConsumer.Received);
                    Assert.AreEqual(1, childConsumer.Calls);
                }
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
