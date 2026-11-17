using System;

namespace GrowthLab.Data
{
    public enum LoadStatus
    {
        Loaded,
        Missing,
        Corrupted,
    }

    public readonly struct LoadResult<T>
    {
        public LoadResult(LoadStatus status, T value, Exception error)
        {
            Status = status;
            Value = value;
            Error = error;
        }

        public LoadStatus Status { get; }

        public T Value { get; }

        /// <summary>Set when <see cref="Status"/> is <see cref="LoadStatus.Corrupted"/>.</summary>
        public Exception Error { get; }

        public bool IsLoaded => Status == LoadStatus.Loaded;
    }

    /// <summary>Typed save/load over a key-value storage and a serializer.</summary>
    public sealed class DataStore
    {
        private readonly IKeyValueStorage _storage;
        private readonly ISerializer _serializer;

        public DataStore(IKeyValueStorage storage, ISerializer serializer = null)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _serializer = serializer ?? new JsonUtilitySerializer();
        }

        public void Save<T>(string key, T value) => _storage.Write(key, _serializer.Serialize(value));

        /// <summary>Never throws for missing or unreadable data; check <see cref="LoadResult{T}.Status"/>.</summary>
        public LoadResult<T> Load<T>(string key)
        {
            if (!_storage.TryRead(key, out var text))
            {
                return new LoadResult<T>(LoadStatus.Missing, default, null);
            }

            try
            {
                return new LoadResult<T>(LoadStatus.Loaded, _serializer.Deserialize<T>(text), null);
            }
            catch (DataFormatException exception)
            {
                return new LoadResult<T>(LoadStatus.Corrupted, default, exception);
            }
        }

        /// <summary>Returns the stored value, or <paramref name="fallback"/> if missing or corrupted.</summary>
        public T LoadOrDefault<T>(string key, T fallback)
        {
            var result = Load<T>(key);
            return result.IsLoaded ? result.Value : fallback;
        }

        public bool Delete(string key) => _storage.Delete(key);

        public bool Exists(string key) => _storage.Exists(key);
    }
}
