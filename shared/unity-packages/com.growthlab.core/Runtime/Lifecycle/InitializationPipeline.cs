using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GrowthLab.Core
{
    public enum PipelineState
    {
        NotStarted,
        Initializing,
        Ready,
        Failed,
        ShutDown,
    }

    /// <summary>
    /// Runs initialization steps one at a time in the order they were added and shuts them down in
    /// reverse order. If a step fails or initialization is cancelled, the steps that already finished
    /// are shut down in reverse order and the original exception is rethrown.
    /// </summary>
    public sealed class InitializationPipeline
    {
        private const string Tag = "Init";

        private readonly List<Step> _steps = new List<Step>();
        private readonly List<Step> _completed = new List<Step>();
        private readonly ILog _log;
        private CancellationTokenSource _runCancellation;

        public InitializationPipeline(ILog log = null)
        {
            _log = log ?? NullLog.Instance;
        }

        public PipelineState State { get; private set; } = PipelineState.NotStarted;

        /// <summary>Names of steps whose initialization completed, in completion order.</summary>
        public IReadOnlyList<string> CompletedSteps
        {
            get
            {
                var names = new string[_completed.Count];
                for (var i = 0; i < names.Length; i++)
                {
                    names[i] = _completed[i].Name;
                }

                return names;
            }
        }

        public InitializationPipeline Add(string name, Func<CancellationToken, Task> initialize, Action shutdown = null)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Step name is required.", nameof(name));
            }

            if (State != PipelineState.NotStarted)
            {
                throw new InvalidOperationException("Steps can only be added before initialization starts.");
            }

            _steps.Add(new Step(name, initialize, shutdown));
            return this;
        }

        /// <summary>Adds an object implementing <see cref="IInitializable"/>, <see cref="IShutdownable"/>, or both.</summary>
        public InitializationPipeline Add(string name, object unit)
        {
            if (unit == null)
            {
                throw new ArgumentNullException(nameof(unit));
            }

            var initializable = unit as IInitializable;
            var shutdownable = unit as IShutdownable;
            if (initializable == null && shutdownable == null)
            {
                throw new ArgumentException("Unit must implement IInitializable or IShutdownable.", nameof(unit));
            }

            Func<CancellationToken, Task> initialize = null;
            if (initializable != null)
            {
                initialize = initializable.InitializeAsync;
            }

            Action shutdown = null;
            if (shutdownable != null)
            {
                shutdown = shutdownable.Shutdown;
            }

            return Add(name, initialize, shutdown);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (State != PipelineState.NotStarted)
            {
                throw new InvalidOperationException("Initialization already ran. State: " + State);
            }

            State = PipelineState.Initializing;
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _runCancellation.Token;
            try
            {
                foreach (var step in _steps)
                {
                    token.ThrowIfCancellationRequested();
                    _log.Debug(Tag, "Initializing " + step.Name);
                    if (step.Initialize != null)
                    {
                        var task = step.Initialize(token);
                        if (task != null)
                        {
                            await task;
                        }
                    }

                    _completed.Add(step);
                }

                token.ThrowIfCancellationRequested();
                State = PipelineState.Ready;
                _log.Info(Tag, "Initialized " + _completed.Count + " step(s).");
            }
            catch (Exception exception)
            {
                var cancelled = exception is OperationCanceledException;
                if (cancelled)
                {
                    _log.Warning(Tag, "Initialization cancelled. Rolling back.");
                }
                else
                {
                    _log.Error(Tag, "Initialization failed. Rolling back.", exception);
                }

                ShutdownCompleted();
                State = PipelineState.Failed;
                throw;
            }
            finally
            {
                _runCancellation.Dispose();
                _runCancellation = null;
            }
        }

        /// <summary>
        /// Shuts down completed steps in reverse order. Exceptions from individual steps are logged
        /// and do not stop the remaining shutdowns. Calling this while initializing requests
        /// cancellation; rollback then happens when the running step returns.
        /// </summary>
        public void Shutdown()
        {
            switch (State)
            {
                case PipelineState.Initializing:
                    _runCancellation?.Cancel();
                    return;
                case PipelineState.Ready:
                    ShutdownCompleted();
                    State = PipelineState.ShutDown;
                    return;
                case PipelineState.NotStarted:
                    State = PipelineState.ShutDown;
                    return;
                default:
                    return;
            }
        }

        private void ShutdownCompleted()
        {
            for (var i = _completed.Count - 1; i >= 0; i--)
            {
                var step = _completed[i];
                if (step.Shutdown == null)
                {
                    continue;
                }

                try
                {
                    _log.Debug(Tag, "Shutting down " + step.Name);
                    step.Shutdown();
                }
                catch (Exception exception)
                {
                    _log.Error(Tag, "Shutdown failed: " + step.Name, exception);
                }
            }

            _completed.Clear();
        }

        private sealed class Step
        {
            public Step(string name, Func<CancellationToken, Task> initialize, Action shutdown)
            {
                Name = name;
                Initialize = initialize;
                Shutdown = shutdown;
            }

            public string Name { get; }

            public Func<CancellationToken, Task> Initialize { get; }

            public Action Shutdown { get; }
        }
    }
}
