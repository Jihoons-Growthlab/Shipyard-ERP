using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrowthLab.Core
{
    public interface ISceneLoadOperation
    {
        /// <summary>0..1. Unity reports up to 0.9 while loading and 1 once activated.</summary>
        float Progress { get; }

        bool IsDone { get; }
    }

    /// <summary>Starts scene operations. Replace in tests to run loader logic without real scenes.</summary>
    public interface ISceneLoadBackend
    {
        /// <exception cref="ArgumentException">The scene cannot be loaded (for example, not in build settings).</exception>
        ISceneLoadOperation Load(string sceneName, LoadSceneMode mode);

        ISceneLoadOperation Unload(string sceneName);
    }

    public readonly struct SceneLoadStarted
    {
        public SceneLoadStarted(string sceneName, LoadSceneMode mode)
        {
            SceneName = sceneName;
            Mode = mode;
        }

        public string SceneName { get; }

        public LoadSceneMode Mode { get; }
    }

    public readonly struct SceneLoadCompleted
    {
        public SceneLoadCompleted(string sceneName, LoadSceneMode mode)
        {
            SceneName = sceneName;
            Mode = mode;
        }

        public string SceneName { get; }

        public LoadSceneMode Mode { get; }
    }

    public interface ISceneLoader
    {
        bool IsBusy { get; }

        Task LoadAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Single, IProgress<float> progress = null,
            CancellationToken cancellationToken = default);

        Task UnloadAsync(string sceneName, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Loads one scene operation at a time. Cancellation stops the wait and throws
    /// <see cref="OperationCanceledException"/>, but Unity cannot abort a started load: the operation
    /// keeps running and <see cref="IsBusy"/> stays true until it finishes.
    /// </summary>
    public sealed class SceneLoader : ISceneLoader
    {
        private const string Tag = "Scenes";

        private readonly ISceneLoadBackend _backend;
        private readonly Func<Task> _nextFrame;
        private readonly IEventBus _events;
        private readonly ILog _log;
        private ISceneLoadOperation _current;

        /// <param name="backend">Defaults to <see cref="UnitySceneLoadBackend"/>.</param>
        /// <param name="nextFrame">Awaited between progress polls. Defaults to <see cref="Awaitable.NextFrameAsync"/>.</param>
        public SceneLoader(ISceneLoadBackend backend = null, Func<Task> nextFrame = null, IEventBus events = null,
            ILog log = null)
        {
            _backend = backend ?? new UnitySceneLoadBackend();
            _nextFrame = nextFrame ?? NextFrameAsync;
            _events = events;
            _log = log ?? NullLog.Instance;
        }

        public bool IsBusy => _current != null && !_current.IsDone;

        /// <exception cref="ArgumentException">Thrown synchronously for an empty name, or by the task if the scene cannot load.</exception>
        /// <exception cref="InvalidOperationException">Thrown synchronously while another operation is running.</exception>
        public Task LoadAsync(string sceneName, LoadSceneMode mode = LoadSceneMode.Single,
            IProgress<float> progress = null, CancellationToken cancellationToken = default)
        {
            ValidateStart(sceneName);
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : LoadCore(sceneName, mode, progress, cancellationToken);
        }

        public Task UnloadAsync(string sceneName, CancellationToken cancellationToken = default)
        {
            ValidateStart(sceneName);
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : UnloadCore(sceneName, cancellationToken);
        }

        private async Task LoadCore(string sceneName, LoadSceneMode mode, IProgress<float> progress,
            CancellationToken cancellationToken)
        {
            _log.Info(Tag, "Loading " + sceneName + " (" + mode + ")");
            var operation = Start(_backend.Load(sceneName, mode), sceneName);
            _events?.Publish(new SceneLoadStarted(sceneName, mode));

            await Wait(operation, sceneName, progress, cancellationToken);

            _events?.Publish(new SceneLoadCompleted(sceneName, mode));
            _log.Info(Tag, "Loaded " + sceneName);
        }

        private async Task UnloadCore(string sceneName, CancellationToken cancellationToken)
        {
            _log.Info(Tag, "Unloading " + sceneName);
            var operation = Start(_backend.Unload(sceneName), sceneName);
            await Wait(operation, sceneName, null, cancellationToken);
        }

        private void ValidateStart(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
            {
                throw new ArgumentException("Scene name is required.", nameof(sceneName));
            }

            if (IsBusy)
            {
                throw new InvalidOperationException("Another scene operation is in progress.");
            }
        }

        private ISceneLoadOperation Start(ISceneLoadOperation operation, string sceneName)
        {
            _current = operation ?? throw new ArgumentException("Scene operation could not start: " + sceneName,
                nameof(sceneName));
            return operation;
        }

        private async Task Wait(ISceneLoadOperation operation, string sceneName, IProgress<float> progress,
            CancellationToken cancellationToken)
        {
            while (!operation.IsDone)
            {
                progress?.Report(Mathf.Clamp01(operation.Progress));
                if (cancellationToken.IsCancellationRequested)
                {
                    _log.Warning(Tag, "Stopped waiting for " + sceneName + "; the Unity operation continues.");
                    cancellationToken.ThrowIfCancellationRequested();
                }

                await _nextFrame();
            }

            progress?.Report(1f);
        }

        private static async Task NextFrameAsync() => await Awaitable.NextFrameAsync();
    }

    /// <summary>Default backend over <see cref="SceneManager"/>.</summary>
    public sealed class UnitySceneLoadBackend : ISceneLoadBackend
    {
        public ISceneLoadOperation Load(string sceneName, LoadSceneMode mode) =>
            Wrap(SceneManager.LoadSceneAsync(sceneName, mode), sceneName);

        public ISceneLoadOperation Unload(string sceneName) =>
            Wrap(SceneManager.UnloadSceneAsync(sceneName), sceneName);

        private static ISceneLoadOperation Wrap(AsyncOperation operation, string sceneName)
        {
            if (operation == null)
            {
                throw new ArgumentException("Scene is not loadable or not loaded: " + sceneName, nameof(sceneName));
            }

            return new Operation(operation);
        }

        private sealed class Operation : ISceneLoadOperation
        {
            private readonly AsyncOperation _operation;

            public Operation(AsyncOperation operation)
            {
                _operation = operation;
            }

            public float Progress => _operation.progress;

            public bool IsDone => _operation.isDone;
        }
    }
}
