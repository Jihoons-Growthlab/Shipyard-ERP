using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace GrowthLab.Core.Tests
{
    public sealed class SettingsStoreTests
    {
        [Serializable]
        public sealed class AudioSettings
        {
            public float MasterVolume = 1f;
            public bool Muted;
        }

        [Test]
        public void Get_ReturnsCopy_SoCallerCannotMutateStore()
        {
            var store = new SettingsStore();
            store.Set(new AudioSettings { MasterVolume = 0.5f });

            var copy = store.Get<AudioSettings>();
            copy.MasterVolume = 0f;

            Assert.AreEqual(0.5f, store.Get<AudioSettings>().MasterVolume);
        }

        [Test]
        public void ApplyJson_OverridesOnlyPresentFields()
        {
            var store = new SettingsStore();
            store.Set(new AudioSettings { MasterVolume = 0.3f, Muted = false });

            var result = store.ApplyJson<AudioSettings>("{\"Muted\":true}");

            Assert.IsTrue(result.Muted);
            Assert.AreEqual(0.3f, result.MasterVolume);
        }

        [Test]
        public void ApplyJson_WithoutSection_StartsFromDefaults()
        {
            var store = new SettingsStore();
            var result = store.ApplyJson<AudioSettings>("{\"Muted\":true}");

            Assert.AreEqual(1f, result.MasterVolume);
            Assert.IsTrue(result.Muted);
        }

        [Test]
        public void Set_PublishesChange()
        {
            var bus = new EventBus();
            var store = new SettingsStore(bus);
            var received = new List<float>();
            bus.Subscribe<SettingsChanged<AudioSettings>>(e => received.Add(e.Value.MasterVolume));

            store.Set(new AudioSettings { MasterVolume = 0.2f });

            CollectionAssert.AreEqual(new[] { 0.2f }, received);
        }

        [Test]
        public void SetDefaults_AppliesOptionalOverride()
        {
            var store = new SettingsStore();

            Assert.AreEqual(1f, store.SetDefaults(new AudioSettings(), null).MasterVolume);
            var overridden = store.SetDefaults(new AudioSettings(), "{\"MasterVolume\":0.25}");

            Assert.AreEqual(0.25f, overridden.MasterVolume);
            Assert.IsFalse(overridden.Muted);
        }

        [Test]
        public void MissingSection_GetThrows_GetOrDefaultReturnsFallback()
        {
            var store = new SettingsStore();
            var fallback = new AudioSettings();

            Assert.Throws<KeyNotFoundException>(() => store.Get<AudioSettings>());
            Assert.AreSame(fallback, store.GetOrDefault(fallback));
        }
    }
}
