using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>
    /// Turns a <c>*.build.json</c> spec into prefabs, one scene and UI documents. Running it again
    /// regenerates everything from the spec, so the spec and CSV are the source of truth.
    /// Menu: GrowthLab > Authoring, or right-click a spec > GrowthLab > Build From Spec. Batch mode:
    /// <c>-executeMethod GrowthLab.Authoring.Editor.SpecBuilder.BuildBatch [-spec &lt;path&gt;]</c>.
    /// </summary>
    public static class SpecBuilder
    {
        public const string SpecSuffix = ".build.json";
        public const string GrowthLabThemePath = "Packages/com.growthlab.ui/Runtime/Resources/GrowthLabRuntimeTheme.tss";

        public sealed class Result
        {
            public string Spec;
            public string Scene;
            public readonly List<string> Prefabs = new List<string>();
            public readonly List<string> Errors = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public int Instances;
            public int Objects;
            public int Documents;

            public bool Succeeded => Errors.Count == 0;

            public override string ToString()
            {
                var text = new StringBuilder();
                text.Append(Succeeded ? "[Authoring] OK  " : "[Authoring] FAILED  ").Append(Spec)
                    .Append(" → ").Append(Scene ?? "-")
                    .Append(" | prefabs ").Append(Prefabs.Count)
                    .Append(", placed ").Append(Instances)
                    .Append(", objects ").Append(Objects)
                    .Append(", ui ").Append(Documents);
                foreach (var error in Errors)
                {
                    text.Append("\n  ERROR ").Append(error);
                }

                foreach (var warning in Warnings)
                {
                    text.Append("\n  WARN  ").Append(warning);
                }

                return text.ToString();
            }
        }

        // ───────────────────────────── Menus and batch ─────────────────────────────

        [MenuItem("Assets/GrowthLab/Build From Spec", true)]
        private static bool ValidateBuildSelected() =>
            Selection.activeObject != null && AssetDatabase.GetAssetPath(Selection.activeObject).EndsWith(SpecSuffix, StringComparison.Ordinal);

        [MenuItem("Assets/GrowthLab/Build From Spec")]
        private static void BuildSelected()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Log(Build(AssetDatabase.GetAssetPath(Selection.activeObject), openScene: true));
            }
        }

        [MenuItem("GrowthLab/Authoring/Build All Specs")]
        public static void BuildAllMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            var specs = FindSpecs();
            if (specs.Count == 0)
            {
                Debug.LogWarning("[Authoring] No *" + SpecSuffix + " files found. Use GrowthLab > Authoring > New Spec.");
            }

            for (var i = 0; i < specs.Count; i++)
            {
                Log(Build(specs[i], openScene: i == specs.Count - 1));
            }
        }

        /// <summary>Batch entry. <c>-spec &lt;path&gt;</c> (repeatable) or every spec in the project. Exit 0 when all succeed.</summary>
        public static void BuildBatch()
        {
            var ok = true;
            try
            {
                var specs = ArgumentValues("-spec");
                if (specs.Count == 0)
                {
                    specs = FindSpecs();
                }

                if (specs.Count == 0)
                {
                    Debug.LogError("[Authoring] No specs found.");
                    ok = false;
                }

                foreach (var spec in specs)
                {
                    var result = Build(spec);
                    Log(result);
                    ok &= result.Succeeded;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ok = false;
            }

            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static List<string> FindSpecs() =>
            AssetDatabase.GetAllAssetPaths().Where(p => p.EndsWith(SpecSuffix, StringComparison.Ordinal)).OrderBy(p => p).ToList();

        // ───────────────────────────── Build ─────────────────────────────

        /// <summary>Builds one spec. Never throws for spec problems; check <see cref="Result.Errors"/>.</summary>
        public static Result Build(string specPath, bool openScene = false)
        {
            var result = new Result { Spec = specPath };
            var spec = Load(specPath, result);
            if (spec == null)
            {
                return result;
            }

            var specFolder = Path.GetDirectoryName(specPath)?.Replace('\\', '/') ?? "Assets";
            string Resolve(string path) => ResolvePath(specFolder, path);

            if (string.IsNullOrEmpty(spec.Scene) || !spec.Scene.StartsWith("Assets/", StringComparison.Ordinal) ||
                !spec.Scene.EndsWith(".unity", StringComparison.Ordinal))
            {
                result.Errors.Add("Scene must be an Assets/... .unity path, was '" + spec.Scene + "'.");
                return result;
            }

            result.Scene = spec.Scene;
            var output = string.IsNullOrEmpty(spec.OutputFolder) ? Path.GetDirectoryName(spec.Scene)?.Replace('\\', '/') : spec.OutputFolder.TrimEnd('/');
            if (output == null || !output.StartsWith("Assets", StringComparison.Ordinal))
            {
                result.Errors.Add("OutputFolder must be inside Assets, was '" + output + "'.");
                return result;
            }

            EnsureFolder(Path.GetDirectoryName(spec.Scene)?.Replace('\\', '/'));
            var prefabs = BuildPrefabs(spec, output, Resolve, result);
            BuildScene(spec, output, prefabs, Resolve, result);

            if (result.Succeeded && spec.AddToBuildSettings)
            {
                var others = EditorBuildSettings.scenes.Where(s => s.path != spec.Scene);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(spec.Scene, true) }.Concat(others).ToArray();
            }

            AssetDatabase.SaveAssets();
            if (openScene && !Application.isBatchMode && File.Exists(spec.Scene))
            {
                EditorSceneManager.OpenScene(spec.Scene, OpenSceneMode.Single);
            }

            return result;
        }

        private static BuildSpec Load(string specPath, Result result)
        {
            string text = null;
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(specPath);
            if (asset != null)
            {
                text = asset.text;
            }
            else if (File.Exists(specPath))
            {
                text = File.ReadAllText(specPath);
            }

            if (text == null)
            {
                result.Errors.Add("Spec not found: " + specPath);
                return null;
            }

            try
            {
                var spec = JsonUtility.FromJson<BuildSpec>(text);
                if (spec == null)
                {
                    result.Errors.Add("Spec is empty: " + specPath);
                }

                return spec;
            }
            catch (ArgumentException exception)
            {
                result.Errors.Add("Spec is not valid JSON (" + exception.Message + "). Keys are case-sensitive: Scene, Prefabs, Ui...");
                return null;
            }
        }

        private static Dictionary<string, GameObject> BuildPrefabs(BuildSpec spec, string output, Func<string, string> resolve,
            Result result)
        {
            var built = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            if (spec.Prefabs == null || spec.Prefabs.Length == 0)
            {
                return built;
            }

            var prefabFolder = output + "/Prefabs";
            EnsureFolder(prefabFolder);
            foreach (var definition in spec.Prefabs)
            {
                var context = "prefab '" + definition?.Name + "'";
                if (definition == null || string.IsNullOrEmpty(definition.Name))
                {
                    result.Errors.Add("A prefab has no Name.");
                    continue;
                }

                if (built.ContainsKey(definition.Name))
                {
                    result.Errors.Add(context + ": duplicate name.");
                    continue;
                }

                var root = CreateVisual(definition, resolve, output, result, context);
                TypeLookup.AddComponents(root, definition.Components, result.Errors, context);
                FieldSetter.ApplyAll(root, definition.Set, resolve, result.Errors, context);

                var path = prefabFolder + "/" + definition.Name + ".prefab";
                var asset = PrefabUtility.SaveAsPrefabAsset(root, path, out var saved);
                UnityEngine.Object.DestroyImmediate(root);
                if (!saved || asset == null)
                {
                    result.Errors.Add(context + ": could not save " + path);
                    continue;
                }

                built[definition.Name] = asset;
                result.Prefabs.Add(path);
            }

            return built;
        }

        private static GameObject CreateVisual(PrefabSpec definition, Func<string, string> resolve, string output, Result result,
            string context)
        {
            GameObject root;
            if (!string.IsNullOrEmpty(definition.Model))
            {
                var modelPath = resolve(definition.Model);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (model != null)
                {
                    root = new GameObject(definition.Name);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
                    instance.name = "Model";
                    instance.transform.SetParent(root.transform, false);
                    if (TryScale(definition.Scale, out var modelScale))
                    {
                        instance.transform.localScale = modelScale;
                    }

                    return root;
                }

                result.Warnings.Add(context + ": model not found at " + modelPath + "; using shape " + definition.Shape + ".");
            }

            var shape = string.IsNullOrEmpty(definition.Shape) ? "Cube" : definition.Shape;
            if (string.Equals(shape, "Empty", StringComparison.OrdinalIgnoreCase))
            {
                root = new GameObject(definition.Name);
            }
            else if (Enum.TryParse(shape, true, out PrimitiveType primitive))
            {
                root = GameObject.CreatePrimitive(primitive);
                root.name = definition.Name;
            }
            else
            {
                result.Errors.Add(context + ": unknown Shape '" + shape + "'. Use Cube, Sphere, Capsule, Cylinder, Plane, Quad or Empty.");
                root = new GameObject(definition.Name);
            }

            if (TryScale(definition.Scale, out var scale))
            {
                root.transform.localScale = scale;
            }

            if (!string.IsNullOrEmpty(definition.Color) && root.TryGetComponent<Renderer>(out var renderer))
            {
                if (ColorUtility.TryParseHtmlString(definition.Color, out var color))
                {
                    renderer.sharedMaterial = ColorMaterial(output, definition.Name, renderer.sharedMaterial, color);
                }
                else
                {
                    result.Errors.Add(context + ": Color '" + definition.Color + "' is not like #3B82F6.");
                }
            }

            return root;
        }

        private static Material ColorMaterial(string output, string name, Material template, Color color)
        {
            var folder = output + "/Materials";
            EnsureFolder(folder);
            var path = folder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                // Copy the pipeline's default material so this works in Built-in, URP and HDRP.
                material = new Material(template);
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void BuildScene(BuildSpec spec, string output, Dictionary<string, GameObject> prefabs,
            Func<string, string> resolve, Result result)
        {
            var previous = SceneManager.GetActiveScene();
            var existing = SceneManager.GetSceneByPath(spec.Scene);
            // Unity refuses additive scenes while an unsaved untitled scene is open; replace it instead.
            var additive = !HasUnsavedUntitledScene();
            if (additive && existing.IsValid() && existing.isLoaded)
            {
                if (SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.CloseScene(existing, true);
                }
                else
                {
                    additive = false;
                }
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, additive ? NewSceneMode.Additive : NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            try
            {
                CreateCameraAndLight(spec);
                CreateObjects(spec, prefabs, resolve, result);
                PlaceFromTable(spec, prefabs, resolve, result);
                CreateUi(spec, output, resolve, result);

                if (!EditorSceneManager.SaveScene(scene, spec.Scene))
                {
                    result.Errors.Add("Could not save scene " + spec.Scene);
                }
            }
            finally
            {
                if (additive)
                {
                    if (previous.IsValid() && previous.isLoaded)
                    {
                        SceneManager.SetActiveScene(previous);
                    }

                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static bool HasUnsavedUntitledScene()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var open = SceneManager.GetSceneAt(i);
                if (string.IsNullOrEmpty(open.path))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CreateCameraAndLight(BuildSpec spec)
        {
            var cameraSpec = spec.Camera ?? new CameraSpec();
            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
            if (TryVector(cameraSpec.Position, out var position))
            {
                cameraObject.transform.position = position;
            }

            if (TryVector(cameraSpec.LookAt, out var lookAt))
            {
                cameraObject.transform.LookAt(lookAt);
            }

            if (!string.IsNullOrEmpty(cameraSpec.Background) && ColorUtility.TryParseHtmlString(cameraSpec.Background, out var background))
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
            }

            var lightSpec = spec.Light ?? new LightSpec();
            var lightObject = new GameObject("Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = lightSpec.Intensity;
            if (TryVector(lightSpec.Rotation, out var rotation))
            {
                lightObject.transform.rotation = Quaternion.Euler(rotation);
            }
        }

        private static void CreateObjects(BuildSpec spec, Dictionary<string, GameObject> prefabs, Func<string, string> resolve,
            Result result)
        {
            if (spec.Objects == null)
            {
                return;
            }

            foreach (var definition in spec.Objects)
            {
                var context = "object '" + definition?.Name + "'";
                if (definition == null || string.IsNullOrEmpty(definition.Name))
                {
                    result.Errors.Add("An object has no Name.");
                    continue;
                }

                GameObject go;
                if (!string.IsNullOrEmpty(definition.Prefab))
                {
                    if (!prefabs.TryGetValue(definition.Prefab, out var prefab))
                    {
                        result.Errors.Add(context + ": unknown prefab '" + definition.Prefab + "'.");
                        continue;
                    }

                    go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
                    go.name = definition.Name;
                }
                else
                {
                    go = new GameObject(definition.Name);
                }

                if (TryVector(definition.Position, out var position))
                {
                    go.transform.position = position;
                }

                if (TryVector(definition.Rotation, out var rotation))
                {
                    go.transform.rotation = Quaternion.Euler(rotation);
                }

                TypeLookup.AddComponents(go, definition.Components, result.Errors, context);
                FieldSetter.ApplyAll(go, definition.Set, resolve, result.Errors, context);
                result.Objects++;
            }
        }

        private static void PlaceFromTable(BuildSpec spec, Dictionary<string, GameObject> prefabs, Func<string, string> resolve,
            Result result)
        {
            if (string.IsNullOrEmpty(spec.Placements))
            {
                return;
            }

            var path = resolve(spec.Placements);
            var text = ReadText(path);
            if (text == null)
            {
                result.Errors.Add("Placements file not found: " + path);
                return;
            }

            var table = PlacementTable.Parse(text, Path.GetFileName(path));
            result.Errors.AddRange(table.Errors);
            var names = new HashSet<string>();
            foreach (var row in table.Rows)
            {
                var context = Path.GetFileName(path) + ":" + row.Line + " '" + row.Name + "'";
                if (!prefabs.TryGetValue(row.Prefab, out var prefab))
                {
                    result.Errors.Add(context + ": unknown prefab '" + row.Prefab + "'. Defined: " + string.Join(", ", prefabs.Keys));
                    continue;
                }

                if (!names.Add(row.Name))
                {
                    result.Warnings.Add(context + ": duplicate name.");
                }

                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
                go.name = row.Name;
                go.transform.SetPositionAndRotation(new Vector3(row.X, row.Y, row.Z), Quaternion.Euler(0f, row.RotY, 0f));
                foreach (var field in row.Fields)
                {
                    FieldSetter.Apply(go, field.Key, field.Value, resolve, result.Errors, context);
                }

                result.Instances++;
            }
        }

        private static void CreateUi(BuildSpec spec, string output, Func<string, string> resolve, Result result)
        {
            if (spec.Ui == null || spec.Ui.Length == 0)
            {
                return;
            }

            var panel = EnsurePanelSettings(output, result);
            foreach (var definition in spec.Ui)
            {
                var context = "ui '" + definition?.Name + "'";
                if (definition == null || string.IsNullOrEmpty(definition.Uxml))
                {
                    result.Errors.Add(context + ": Uxml is required.");
                    continue;
                }

                var uxmlPath = resolve(definition.Uxml);
                var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                if (tree == null)
                {
                    result.Errors.Add(context + ": no UXML at " + uxmlPath + " (or it failed to import; check the Console).");
                    continue;
                }

                var go = new GameObject(string.IsNullOrEmpty(definition.Name) ? "UI" : definition.Name);
                var document = go.AddComponent<UIDocument>();
                document.panelSettings = panel;
                document.visualTreeAsset = tree;
                document.sortingOrder = definition.SortingOrder;
                TypeLookup.AddComponents(go, definition.Components, result.Errors, context);
                FieldSetter.ApplyAll(go, definition.Set, resolve, result.Errors, context);
                result.Documents++;
            }
        }

        private static PanelSettings EnsurePanelSettings(string output, Result result)
        {
            var folder = output + "/UI";
            EnsureFolder(folder);
            var path = folder + "/PanelSettings.asset";
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (panel != null)
            {
                return panel;
            }

            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(GrowthLabThemePath);
            if (theme == null)
            {
                var defaultTheme = folder + "/DefaultRuntimeTheme.tss";
                if (!File.Exists(defaultTheme))
                {
                    File.WriteAllText(defaultTheme, "@import url(\"unity-theme://default\");\n");
                    AssetDatabase.ImportAsset(defaultTheme);
                }

                theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(defaultTheme);
                result.Warnings.Add("com.growthlab.ui is not installed; UI uses Unity's default runtime theme.");
            }

            panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1600, 900);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 1f;
            AssetDatabase.CreateAsset(panel, path);
            return panel;
        }

        // ───────────────────────────── Helpers ─────────────────────────────

        public static string ResolvePath(string specFolder, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = path.Replace('\\', '/');
            if (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return path;
            }

            var parts = new List<string>(specFolder.Split('/'));
            foreach (var part in path.Split('/'))
            {
                if (part == "..")
                {
                    if (parts.Count > 1)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                }
                else if (part != "." && part.Length > 0)
                {
                    parts.Add(part);
                }
            }

            return string.Join("/", parts);
        }

        private static string ReadText(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            if (asset != null)
            {
                return asset.text;
            }

            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static bool TryVector(float[] values, out Vector3 vector)
        {
            vector = default;
            if (values == null || values.Length < 3)
            {
                return false;
            }

            vector = new Vector3(values[0], values[1], values[2]);
            return true;
        }

        private static bool TryScale(float[] values, out Vector3 scale)
        {
            scale = Vector3.one;
            if (values == null || values.Length == 0)
            {
                return false;
            }

            scale = values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : Vector3.one * values[0];
            return true;
        }

        private static List<string> ArgumentValues(string name)
        {
            var values = new List<string>();
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    values.Add(args[i + 1]);
                }
            }

            return values;
        }

        private static void Log(Result result)
        {
            if (result.Succeeded)
            {
                Debug.Log(result.ToString());
            }
            else
            {
                Debug.LogError(result.ToString());
            }
        }
    }
}
