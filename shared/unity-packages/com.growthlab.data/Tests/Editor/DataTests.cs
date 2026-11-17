using System;
using System.IO;
using NUnit.Framework;

namespace GrowthLab.Data.Tests
{
    public sealed class DataTests
    {
        [Serializable]
        public sealed class SaveGame
        {
            public int Level;
            public string Name;
        }

        private string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "growthlab-data-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void FileStorage_SaveLoadRoundTrip_AndOverwrite()
        {
            var store = new DataStore(new FileStorage(_root));
            store.Save("slot1", new SaveGame { Level = 3, Name = "한글" });
            store.Save("slot1", new SaveGame { Level = 4, Name = "한글" });

            var result = store.Load<SaveGame>("slot1");

            Assert.AreEqual(LoadStatus.Loaded, result.Status);
            Assert.AreEqual(4, result.Value.Level);
            Assert.AreEqual("한글", result.Value.Name);
            Assert.IsFalse(File.Exists(Path.Combine(_root, "slot1.json.tmp")), "Temp file must not remain.");
        }

        [Test]
        public void Missing_ReturnsMissing()
        {
            var store = new DataStore(new FileStorage(_root));
            Assert.AreEqual(LoadStatus.Missing, store.Load<SaveGame>("none").Status);
            Assert.IsFalse(store.Delete("none"));
        }

        [Test]
        public void CorruptedFile_ReturnsCorrupted_AndFallbackIsUsed()
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "slot1.json"), "{not json");
            var store = new DataStore(new FileStorage(_root));

            var result = store.Load<SaveGame>("slot1");
            var fallback = new SaveGame { Level = 1 };

            Assert.AreEqual(LoadStatus.Corrupted, result.Status);
            Assert.IsInstanceOf<DataFormatException>(result.Error);
            Assert.AreSame(fallback, store.LoadOrDefault("slot1", fallback));
        }

        [TestCase("../escape")]
        [TestCase("a/b")]
        [TestCase(".hidden")]
        [TestCase("")]
        [TestCase("with space")]
        public void InvalidKeys_AreRejected(string key)
        {
            Assert.Throws<ArgumentException>(() => new FileStorage(_root).Write(key, "{}"));
            Assert.Throws<ArgumentException>(() => new MemoryStorage().Write(key, "{}"));
        }

        [Test]
        public void Delete_RemovesFile()
        {
            var storage = new FileStorage(_root);
            storage.Write("k", "{}");
            Assert.IsTrue(storage.Exists("k"));
            Assert.IsTrue(storage.Delete("k"));
            Assert.IsFalse(storage.Exists("k"));
        }

        [Test]
        public void MemoryStorage_WorksWithDataStore()
        {
            var store = new DataStore(new MemoryStorage());
            store.Save("s", new SaveGame { Level = 9 });
            Assert.AreEqual(9, store.LoadOrDefault<SaveGame>("s", null).Level);
        }

        [Test]
        public void Serializer_EmptyText_Throws()
        {
            Assert.Throws<DataFormatException>(() => new JsonUtilitySerializer().Deserialize<SaveGame>(" "));
        }
    }
}
