using System;
using UnityEngine;

namespace GrowthLab.Data
{
    public interface ISerializer
    {
        string Serialize<T>(T value);

        /// <exception cref="DataFormatException">The text is not valid for <typeparamref name="T"/>.</exception>
        T Deserialize<T>(string text);
    }

    public sealed class DataFormatException : Exception
    {
        public DataFormatException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// <see cref="JsonUtility"/> rules apply: [Serializable] classes or structs with public or
    /// [SerializeField] fields; top-level arrays, dictionaries and primitives are not supported.
    /// </summary>
    public sealed class JsonUtilitySerializer : ISerializer
    {
        private readonly bool _prettyPrint;

        public JsonUtilitySerializer(bool prettyPrint = false)
        {
            _prettyPrint = prettyPrint;
        }

        public string Serialize<T>(T value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return JsonUtility.ToJson(value, _prettyPrint);
        }

        public T Deserialize<T>(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new DataFormatException("Empty JSON for " + typeof(T).Name + ".");
            }

            try
            {
                var value = JsonUtility.FromJson<T>(text);
                if (value == null)
                {
                    throw new DataFormatException("JSON produced no value for " + typeof(T).Name + ".");
                }

                return value;
            }
            catch (ArgumentException exception)
            {
                throw new DataFormatException("Invalid JSON for " + typeof(T).Name + ".", exception);
            }
        }
    }
}
