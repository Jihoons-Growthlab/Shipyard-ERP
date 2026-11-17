using System;
using System.Collections.Generic;

namespace GrowthLab.Core
{
    public interface IEventBus
    {
        /// <summary>
        /// Subscribes to events of exactly type <typeparamref name="T"/>.
        /// Dispose the returned handle to unsubscribe; disposing twice is a no-op.
        /// </summary>
        IDisposable Subscribe<T>(Action<T> handler);

        /// <summary>
        /// Delivers <paramref name="message"/> to the handlers registered when publishing started.
        /// A throwing handler is logged and does not stop the remaining handlers.
        /// </summary>
        void Publish<T>(T message);

        int SubscriberCount<T>();
    }

    /// <summary>
    /// Single-threaded, type-keyed event bus. Subscribing or unsubscribing inside a handler is allowed:
    /// new subscribers receive the next publish, and removed subscribers stop receiving immediately.
    /// </summary>
    public sealed class EventBus : IEventBus
    {
        private const string Tag = "EventBus";

        private readonly Dictionary<Type, List<Subscription>> _subscriptions = new Dictionary<Type, List<Subscription>>();
        private readonly ILog _log;

        public EventBus(ILog log = null)
        {
            _log = log ?? NullLog.Instance;
        }

        public IDisposable Subscribe<T>(Action<T> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!_subscriptions.TryGetValue(typeof(T), out var list))
            {
                list = new List<Subscription>();
                _subscriptions.Add(typeof(T), list);
            }

            var subscription = new Subscription(this, typeof(T), handler);
            list.Add(subscription);
            return subscription;
        }

        public void Publish<T>(T message)
        {
            if (!_subscriptions.TryGetValue(typeof(T), out var list) || list.Count == 0)
            {
                return;
            }

            // Snapshot so handlers may change subscriptions while we iterate.
            var snapshot = list.ToArray();
            foreach (var subscription in snapshot)
            {
                if (!subscription.IsActive)
                {
                    continue;
                }

                try
                {
                    ((Action<T>)subscription.Handler)(message);
                }
                catch (Exception exception)
                {
                    _log.Error(Tag, "Handler for " + typeof(T).Name + " threw.", exception);
                }
            }
        }

        public int SubscriberCount<T>() =>
            _subscriptions.TryGetValue(typeof(T), out var list) ? list.Count : 0;

        /// <summary>Removes every subscription. Existing handles become no-ops.</summary>
        public void Clear()
        {
            foreach (var list in _subscriptions.Values)
            {
                foreach (var subscription in list)
                {
                    subscription.Deactivate();
                }
            }

            _subscriptions.Clear();
        }

        private void Remove(Subscription subscription)
        {
            if (_subscriptions.TryGetValue(subscription.EventType, out var list))
            {
                list.Remove(subscription);
            }
        }

        private sealed class Subscription : IDisposable
        {
            private EventBus _owner;

            public Subscription(EventBus owner, Type eventType, Delegate handler)
            {
                _owner = owner;
                EventType = eventType;
                Handler = handler;
            }

            public Type EventType { get; }

            public Delegate Handler { get; }

            public bool IsActive => _owner != null;

            public void Deactivate() => _owner = null;

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null)
                {
                    return;
                }

                _owner = null;
                owner.Remove(this);
            }
        }
    }
}
