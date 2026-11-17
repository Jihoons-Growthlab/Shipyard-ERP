using System;
using System.Collections.Generic;
using UnityEngine;

namespace GrowthLab.Core
{
    /// <summary>Published on the event bus after a settings section of type <typeparamref name="T"/> changes.</summary>
    public readonly struct SettingsChanged<T> where T : class
    {
        public SettingsChanged(T value)
        {
            Value = value;
        }

        public T Value { get; }
    }

    /// <summary>
    /// Type-keyed settings sections. A section is a [Serializable] class with public fields so it can be
    /// overridden from JSON with <see cref="JsonUtility"/>. Values returned by <see cref="Get{T}"/> are
    /// copies; change settings only through <see cref="Set{T}"/> or <see cref="ApplyJson{T}"/>.
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly Dictionary<Type, string> _sections = new Dictionary<Type, string>();
        private readonly IEventBus _events;

        public SettingsStore(IEventBus events = null)
        {
            _events = events;
        }

        public bool Contains<T>() where T : class => _sections.ContainsKey(typeof(T));

        public void Set<T>(T value) where T : class
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            _sections[typeof(T)] = JsonUtility.ToJson(value);
            _events?.Publish(new SettingsChanged<T>(Get<T>()));
        }

        public T Get<T>() where T : class
        {
            if (!_sections.TryGetValue(typeof(T), out var json))
            {
                throw new KeyNotFoundException("Settings section not set: " + typeof(T).FullName);
            }

            return JsonUtility.FromJson<T>(json);
        }

        public T GetOrDefault<T>(T fallback) where T : class =>
            _sections.TryGetValue(typeof(T), out var json) ? JsonUtility.FromJson<T>(json) : fallback;

        /// <summary>
        /// Overwrites only the fields present in <paramref name="json"/> on top of the current section
        /// (or a new <typeparamref name="T"/> if none is set), then stores the result.
        /// </summary>
        public T ApplyJson<T>(string json) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("JSON is required.", nameof(json));
            }

            var section = Contains<T>() ? Get<T>() : new T();
            JsonUtility.FromJsonOverwrite(json, section);
            Set(section);
            return Get<T>();
        }

        /// <summary>
        /// Stores <paramref name="defaults"/>, then applies <paramref name="overrideJson"/> if it is not empty.
        /// Typical startup call with an optional TextAsset.
        /// </summary>
        public T SetDefaults<T>(T defaults, string overrideJson = null) where T : class, new()
        {
            Set(defaults);
            return string.IsNullOrWhiteSpace(overrideJson) ? Get<T>() : ApplyJson<T>(overrideJson);
        }

        public bool Remove<T>() where T : class => _sections.Remove(typeof(T));
    }
}
