using System;
using System.Collections.Generic;

namespace GrowthLab.Core
{
    /// <summary>
    /// Manual dependency registry. Callers register concrete instances by contract type and pass
    /// them explicitly to constructors; there is no reflection-based injection.
    /// </summary>
    public sealed class ServiceRegistry
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();

        public void Register<T>(T instance) where T : class
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            if (_services.ContainsKey(typeof(T)))
            {
                throw new InvalidOperationException("Service already registered: " + typeof(T).FullName);
            }

            _services.Add(typeof(T), instance);
        }

        public T Get<T>() where T : class
        {
            if (TryGet<T>(out var service))
            {
                return service;
            }

            throw new KeyNotFoundException("Service not registered: " + typeof(T).FullName);
        }

        public bool TryGet<T>(out T service) where T : class
        {
            if (_services.TryGetValue(typeof(T), out var value))
            {
                service = (T)value;
                return true;
            }

            service = null;
            return false;
        }

        public bool Unregister<T>() where T : class => _services.Remove(typeof(T));

        public bool Contains<T>() where T : class => _services.ContainsKey(typeof(T));

        public void Clear() => _services.Clear();
    }
}
