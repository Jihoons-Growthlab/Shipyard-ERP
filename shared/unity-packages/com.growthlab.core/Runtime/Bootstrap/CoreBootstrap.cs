using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrowthLab.Core
{
    /// <summary>
    /// Unity entry point shared by apps. Subclass it, add steps in <see cref="Configure"/>, and place it in
    /// the first scene. Lifecycle: Start → Configure → pipeline initialization → inject
    /// <see cref="ICoreConsumer"/>s in open scenes → <see cref="OnReady"/> → <see cref="WhenReady"/> callbacks.
    /// Every scene loaded afterwards is injected as soon as Unity reports it loaded (before its components'
    /// Start), whether it was loaded through <see cref="CoreContext.Scenes"/> or <see cref="SceneManager"/>.
    /// Objects created at runtime are not scanned: use <see cref="CoreContext.Instantiate"/> or
    /// <see cref="CoreContext.Inject"/>. OnDestroy shuts everything down in reverse order.
    /// Only one bootstrap is active at a time; a second one logs a warning and removes itself.
    /// </summary>
    public abstract class CoreBootstrap : MonoBehaviour
    {
        private const string Tag = "Bootstrap";

        private static readonly List<Action<CoreContext>> PendingReady = new List<Action<CoreContext>>();

        [SerializeField] private LogLevel _logLevel = LogLevel.Info;

        [Tooltip("Keep this object (and the Core services) alive across Single scene loads. Root objects only.")]
        [SerializeField] private bool _persistAcrossScenes = true;

        private bool _duplicate;
        private bool _listeningForScenes;

        /// <summary>The bootstrap that owns Core right now, or null.</summary>
        public static CoreBootstrap Active { get; private set; }

        public CoreContext Context { get; private set; }

        public PipelineState State => Context?.Pipeline.State ?? PipelineState.NotStarted;

        /// <summary>
        /// Runs <paramref name="callback"/> once Core is ready: immediately if it already is, otherwise right
        /// after <see cref="OnReady"/>. For code that cannot implement <see cref="ICoreConsumer"/>.
        /// Callbacks are dropped if initialization fails.
        /// </summary>
        public static void WhenReady(Action<CoreContext> callback)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            if (Active != null && Active.State == PipelineState.Ready)
            {
                callback(Active.Context);
                return;
            }

            PendingReady.Add(callback);
        }

        /// <summary>Register services, settings defaults and pipeline steps. Do not await here.</summary>
        protected abstract void Configure(CoreContext context);

        /// <summary>Runs after initialization and the first injection pass.</summary>
        protected virtual void OnReady(CoreContext context)
        {
        }

        protected virtual void OnInitializationFailed(Exception exception)
        {
            Context?.Log.Error(Tag, "Initialization failed.", exception);
        }

        /// <summary>Override to supply a different log (for example in tests).</summary>
        protected virtual ILog CreateLog() => new UnityLog(_logLevel);

        // Statics survive play sessions when domain reload is disabled (Enter Play Mode Options).
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Active = null;
            PendingReady.Clear();
        }

        protected virtual void Awake()
        {
            if (Active != null && Active != this)
            {
                _duplicate = true;
                Debug.LogWarning("[" + Tag + "] Another CoreBootstrap (" + Active.name + ") is already active; removing " +
                                 GetType().Name + " on '" + name + "'. Keep a single bootstrap in the first scene.", this);
                Destroy(this);
                return;
            }

            Active = this;
        }

        private async void Start()
        {
            if (_duplicate)
            {
                return;
            }

            var persisted = _persistAcrossScenes && transform.parent == null;
            if (persisted)
            {
                DontDestroyOnLoad(gameObject);
            }

            Context = new CoreContext(CreateLog());
            try
            {
                Configure(Context);
                await Context.Pipeline.InitializeAsync(destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Destroyed during initialization; OnDestroy already shut down.
                PendingReady.Clear();
                return;
            }
            catch (Exception exception)
            {
                PendingReady.Clear();
                OnInitializationFailed(exception);
                return;
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            _listeningForScenes = true;
            if (persisted)
            {
                // The DontDestroyOnLoad scene is not listed by SceneManager.GetSceneAt.
                CoreInjection.InjectHierarchy(gameObject, Context);
            }

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                CoreInjection.InjectScene(SceneManager.GetSceneAt(i), Context);
            }

            OnReady(Context);
            RunPendingReady();
        }

        private void RunPendingReady()
        {
            var callbacks = PendingReady.ToArray();
            PendingReady.Clear();
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(Context);
                }
                catch (Exception exception)
                {
                    Context.Log.Error(Tag, "WhenReady callback threw.", exception);
                }
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var count = CoreInjection.InjectScene(scene, Context);
            Context.Log.Debug(Tag, "Injected " + count + " consumer(s) into " + scene.name);
        }

        protected virtual void OnDestroy()
        {
            if (_listeningForScenes)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _listeningForScenes = false;
            }

            if (Active == this)
            {
                Active = null;
            }

            Context?.Dispose();
        }
    }
}
