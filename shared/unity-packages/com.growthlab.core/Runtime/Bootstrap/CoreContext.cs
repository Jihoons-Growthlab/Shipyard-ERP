using System;
using UnityEngine;

namespace GrowthLab.Core
{
    /// <summary>
    /// The standard set of Core services, wired by hand. Registered in <see cref="Services"/> under
    /// <see cref="ILog"/>, <see cref="IEventBus"/>, <see cref="SettingsStore"/> and <see cref="ISceneLoader"/>.
    /// Apps add their own services and pipeline steps before calling <see cref="InitializationPipeline.InitializeAsync"/>.
    /// </summary>
    public sealed class CoreContext : IDisposable
    {
        private readonly EventBus _events;
        private bool _disposed;

        public CoreContext(ILog log = null, ISceneLoader scenes = null)
        {
            Log = log ?? new UnityLog();
            _events = new EventBus(Log);
            Settings = new SettingsStore(_events);
            Scenes = scenes ?? new SceneLoader(events: _events, log: Log);
            Pipeline = new InitializationPipeline(Log);
            Services = new ServiceRegistry();

            Services.Register(Log);
            Services.Register<IEventBus>(_events);
            Services.Register(Settings);
            Services.Register(Scenes);
        }

        public ILog Log { get; }

        public IEventBus Events => _events;

        public SettingsStore Settings { get; }

        public ISceneLoader Scenes { get; }

        public InitializationPipeline Pipeline { get; }

        public ServiceRegistry Services { get; }

        /// <summary>
        /// Instantiates <paramref name="prefab"/> and injects every <see cref="ICoreConsumer"/> in the copy.
        /// Use this for objects created at runtime; only loaded scenes are injected automatically.
        /// </summary>
        public GameObject Instantiate(GameObject prefab, Transform parent = null)
        {
            if (prefab == null)
            {
                throw new ArgumentNullException(nameof(prefab));
            }

            var instance = parent != null ? UnityEngine.Object.Instantiate(prefab, parent) : UnityEngine.Object.Instantiate(prefab);
            CoreInjection.InjectHierarchy(instance, this);
            return instance;
        }

        /// <summary>Injects an object created some other way (for example by a pool).</summary>
        /// <returns>Number of components that received the context.</returns>
        public int Inject(GameObject root) => CoreInjection.InjectHierarchy(root, this);

        /// <summary>Shuts the pipeline down (reverse order) and drops every event subscription.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Pipeline.Shutdown();
            _events.Clear();
        }
    }
}
