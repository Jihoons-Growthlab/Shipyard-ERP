using System;
using System.Collections;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace GrowthLab.Core.Tests
{
    /// <summary>
    /// Loads a real scene through <see cref="UnitySceneLoadBackend"/>. The scene is generated before the
    /// run and added to build settings, then removed afterwards, so the test project needs no fixtures.
    /// Editor-only: player builds do not run the setup, so the scene would be missing there.
    /// </summary>
    [PrebuildSetup(typeof(SceneLoaderPlayModeTests))]
    [PostBuildCleanup(typeof(SceneLoaderPlayModeTests))]
    public sealed class SceneLoaderPlayModeTests : IPrebuildSetup, IPostBuildCleanup
    {
        private const string Folder = "Assets/GrowthLabCoreTests";
        private const string SceneName = "GrowthLabCoreTestScene";
        private const string ScenePath = Folder + "/" + SceneName + ".unity";
        private const string MarkerName = "GrowthLabCoreTestMarker";

        public void Setup()
        {
#if UNITY_EDITOR
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                if (!AssetDatabase.IsValidFolder(Folder))
                {
                    AssetDatabase.CreateFolder("Assets", "GrowthLabCoreTests");
                }

                // Additive: the test runner's own init scene must stay open, replacing it stalls the run.
                var active = SceneManager.GetActiveScene();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                new GameObject(MarkerName).AddComponent<RecordingConsumer>();
                EditorSceneManager.SaveScene(scene, ScenePath);
                SceneManager.SetActiveScene(active);
                EditorSceneManager.CloseScene(scene, true);
            }

            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.All(s => s.path != ScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }
#endif
        }

        public void Cleanup()
        {
#if UNITY_EDITOR
            EditorBuildSettings.scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToArray();
            AssetDatabase.DeleteAsset(Folder);
#endif
        }

        [UnityTest]
        public IEnumerator LoadAdditive_ThenUnload_UsesRealSceneManager()
        {
            var bus = new EventBus();
            var completed = 0;
            bus.Subscribe<SceneLoadCompleted>(_ => completed++);
            var loader = new SceneLoader(events: bus);

            yield return WaitFor(loader.LoadAsync(SceneName, LoadSceneMode.Additive));

            var scene = SceneManager.GetSceneByName(SceneName);
            Assert.IsTrue(scene.isLoaded);
            Assert.IsNotNull(GameObject.Find(MarkerName));
            Assert.AreEqual(1, completed);
            Assert.IsFalse(loader.IsBusy);

            yield return WaitFor(loader.UnloadAsync(SceneName));

            Assert.IsFalse(SceneManager.GetSceneByName(SceneName).isLoaded);
        }

        [UnityTest]
        public IEnumerator SceneNotInBuild_FailsWithArgumentException()
        {
            LogAssert.Expect(LogType.Error, new Regex("couldn't be loaded"));
            var loader = new SceneLoader();

            var task = loader.LoadAsync("GrowthLabSceneThatDoesNotExist", LoadSceneMode.Additive);
            yield return null;

            Assert.IsTrue(task.IsFaulted);
            Assert.IsInstanceOf<ArgumentException>(task.Exception.InnerException);
        }

        [UnityTest]
        public IEnumerator SceneLoadedThroughSceneManager_IsInjected_BeforeStart()
        {
            RecordingBootstrap.Calls = new System.Collections.Generic.List<string>();
            RecordingBootstrap.FailInitialization = false;
            var root = new GameObject("Bootstrap");
            var bootstrap = root.AddComponent<RecordingBootstrap>();
            yield return null;
            yield return null;
            Assert.AreEqual(PipelineState.Ready, bootstrap.State);

            // Plain SceneManager, not CoreContext.Scenes.
            var load = SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Additive);
            while (!load.isDone)
            {
                yield return null;
            }

            yield return null; // Start of the scene's components
            var consumer = GameObject.Find(MarkerName).GetComponent<RecordingConsumer>();
            Assert.AreSame(bootstrap.Context, consumer.Received);
            Assert.AreEqual(1, consumer.ConstructCount);
            Assert.AreEqual(true, consumer.HadContextAtStart, "Construct ran before Start.");

            yield return SceneManager.UnloadSceneAsync(SceneName);
            Object.Destroy(root);
            yield return null;
        }

        private static IEnumerator WaitFor(Task task, float timeoutSeconds = 10f)
        {
            var start = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    Assert.Fail("Timed out after " + timeoutSeconds + "s.");
                }

                yield return null;
            }

            task.GetAwaiter().GetResult();
        }
    }
}
