using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GrowthLab.Core.Editor
{
    /// <summary>
    /// Editor helpers for generating scenes from code (sample setup, test fixtures).
    /// Not named SceneSetup: that would clash with <see cref="UnityEditor.SceneManagement.SceneSetup"/>.
    /// </summary>
    public static class SceneScaffold
    {
        /// <summary>Creates every missing folder in an "Assets/..." path.</summary>
        public static void EnsureFolder(string path)
        {
            if (!path.StartsWith("Assets", StringComparison.Ordinal))
            {
                throw new ArgumentException("Path must start with Assets: " + path, nameof(path));
            }

            var parts = path.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        /// <summary>
        /// Builds a new scene with <paramref name="populate"/>, saves it to <paramref name="path"/>
        /// (overwriting), and returns the path. Replaces the currently open scenes.
        /// </summary>
        public static string CreateScene(string path, Action<Scene> populate, bool withCameraAndLight = true)
        {
            EnsureFolder(path.Substring(0, path.LastIndexOf('/')));
            var scene = EditorSceneManager.NewScene(
                withCameraAndLight ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene, NewSceneMode.Single);
            populate?.Invoke(scene);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                throw new InvalidOperationException("Could not save scene: " + path);
            }

            return path;
        }

        /// <summary>Adds the scenes to build settings in order, keeping unrelated entries after them.</summary>
        public static void SetBuildScenes(params string[] paths)
        {
            var others = EditorBuildSettings.scenes.Where(s => !paths.Contains(s.path));
            EditorBuildSettings.scenes = paths.Select(p => new EditorBuildSettingsScene(p, true)).Concat(others).ToArray();
        }

        public static T AddRoot<T>(string name) where T : Component => new GameObject(name).AddComponent<T>();
    }
}
