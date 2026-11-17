using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace GrowthLab.Data
{
    /// <summary>String storage keyed by simple names. Keys: 1-64 chars of letters, digits, '_', '-', '.'; no leading '.'.</summary>
    public interface IKeyValueStorage
    {
        bool Exists(string key);

        bool TryRead(string key, out string value);

        void Write(string key, string value);

        bool Delete(string key);
    }

    public static class StorageKey
    {
        public static void Validate(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 64 || key[0] == '.')
            {
                throw new ArgumentException("Invalid storage key: '" + key + "'.", nameof(key));
            }

            foreach (var c in key)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                         c == '_' || c == '-' || c == '.';
                if (!ok)
                {
                    throw new ArgumentException("Invalid storage key: '" + key + "'.", nameof(key));
                }
            }
        }
    }

    public sealed class MemoryStorage : IKeyValueStorage
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public bool Exists(string key)
        {
            StorageKey.Validate(key);
            return _values.ContainsKey(key);
        }

        public bool TryRead(string key, out string value)
        {
            StorageKey.Validate(key);
            return _values.TryGetValue(key, out value);
        }

        public void Write(string key, string value)
        {
            StorageKey.Validate(key);
            _values[key] = value ?? throw new ArgumentNullException(nameof(value));
        }

        public bool Delete(string key)
        {
            StorageKey.Validate(key);
            return _values.Remove(key);
        }
    }

    /// <summary>
    /// One UTF-8 file per key under <see cref="RootDirectory"/>. Writes go to a temporary file first and
    /// then replace the target, so a crash mid-write leaves the previous value intact.
    /// </summary>
    public sealed class FileStorage : IKeyValueStorage
    {
        private const string Extension = ".json";
        private const string TempSuffix = ".tmp";
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public FileStorage(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
            {
                throw new ArgumentException("Root directory is required.", nameof(rootDirectory));
            }

            RootDirectory = Path.GetFullPath(rootDirectory);
        }

        /// <summary>Storage under <c>Application.persistentDataPath/&lt;folder&gt;</c>.</summary>
        public static FileStorage InPersistentData(string folder = "growthlab") =>
            new FileStorage(Path.Combine(Application.persistentDataPath, folder));

        public string RootDirectory { get; }

        public bool Exists(string key) => File.Exists(PathFor(key));

        public bool TryRead(string key, out string value)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                value = null;
                return false;
            }

            value = File.ReadAllText(path, Utf8NoBom);
            return true;
        }

        public void Write(string key, string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var path = PathFor(key);
            Directory.CreateDirectory(RootDirectory);
            var temp = path + TempSuffix;
            File.WriteAllText(temp, value, Utf8NoBom);
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        public bool Delete(string key)
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }

        private string PathFor(string key)
        {
            StorageKey.Validate(key);
            return Path.Combine(RootDirectory, key + Extension);
        }
    }
}
