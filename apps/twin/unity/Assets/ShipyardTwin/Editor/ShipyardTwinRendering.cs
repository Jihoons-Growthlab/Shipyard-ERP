using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ShipyardTwin.Editor
{
    /// <summary>
    /// URP setup for the twin, created from code so it is reproducible: pipeline asset (long soft shadows, MSAA, HDR),
    /// renderer with SSAO tuned for a 1 km yard, post-processing volume (ACES, bloom, colour grading, vignette),
    /// procedural sky with baked ambient/reflection. Assets live in Assets/ShipyardTwin/Generated/Rendering.
    /// </summary>
    public static class ShipyardTwinRendering
    {
        public const string Folder = "Assets/ShipyardTwin/Generated/Rendering";
        private const string PipelinePath = Folder + "/ShipyardTwinURP.asset";
        private const string RendererPath = Folder + "/ShipyardTwinURP_Renderer.asset";
        private const string ProfilePath = Folder + "/ShipyardTwinVolume.asset";
        private const string SkyPath = Folder + "/ShipyardTwinSky.mat";

        /// <summary>Makes URP the active pipeline (before the spec build, so generated materials use URP/Lit).</summary>
        public static UniversalRenderPipelineAsset EnsurePipeline()
        {
            EnsureFolder(Folder);
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (asset == null)
            {
                asset = UniversalRenderPipelineAsset.Create();
                AssetDatabase.CreateAsset(asset, PipelinePath);
                asset.LoadBuiltinRendererData();                  // creates Assets/UniversalRenderer.asset with defaults
                AssetDatabase.MoveAsset("Assets/UniversalRenderer.asset", RendererPath);
                AssetDatabase.SaveAssets();
            }

            asset.supportsHDR = true;
            asset.msaaSampleCount = 4;
            asset.mainLightShadowmapResolution = 4096;
            asset.shadowDistance = 1500f;
            asset.shadowCascadeCount = 4;
            var serialized = new SerializedObject(asset);       // read-only properties: set the serialized fields
            serialized.FindProperty("m_MainLightShadowsSupported").boolValue = true;
            serialized.FindProperty("m_SoftShadowsSupported").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AddSsao(AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath));
            Transparent("Foam", Color.white);
            GraphicsSettings.defaultRenderPipeline = asset;
            QualitySettings.renderPipeline = asset;
            AssetDatabase.SaveAssets();
            return asset;
        }

        public const string MaterialFolder = "Assets/ShipyardTwin/Runtime/Resources/ShipyardTwin/Materials";

        /// <summary>Unlit transparent material asset (Resources/ShipyardTwin/Materials/&lt;name&gt;) for foam and shallows.</summary>
        private static void Transparent(string name, Color color)
        {
            EnsureFolder(MaterialFolder);
            var path = MaterialFolder + "/" + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", color);
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            material.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetOverrideTag("RenderType", "Transparent");
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(material);
        }

        private static void AddSsao(UniversalRendererData renderer)
        {
            if (renderer == null || renderer.rendererFeatures.Exists(f => f is ScreenSpaceAmbientOcclusion))
            {
                return;
            }

            var ssao = ScriptableObject.CreateInstance<ScreenSpaceAmbientOcclusion>();
            ssao.name = "SSAO";
            AssetDatabase.AddObjectToAsset(ssao, renderer);
            renderer.rendererFeatures.Add(ssao);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(ssao, out _, out long localId);
            var serialized = new SerializedObject(renderer);
            var map = serialized.FindProperty("m_RendererFeatureMap");
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Defaults suit metre-scale props; a 1 km yard needs a wider radius and a longer falloff.
            var settings = new SerializedObject(ssao);
            settings.FindProperty("m_Settings.Radius").floatValue = 1.6f;
            settings.FindProperty("m_Settings.Intensity").floatValue = 1.6f;
            settings.FindProperty("m_Settings.DirectLightingStrength").floatValue = 0.35f;
            settings.FindProperty("m_Settings.Falloff").floatValue = 900f;
            settings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(renderer);
        }

        /// <summary>Adds sky, sun link, post-processing volume and bakes ambient + reflection for the open scene.</summary>
        public static void DecorateScene(string scenePath)
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, SkyPath);
            }

            sky.SetFloat("_SunSize", 0.035f);
            sky.SetFloat("_AtmosphereThickness", 0.85f);
            sky.SetColor("_SkyTint", new Color(0.52f, 0.62f, 0.75f));
            sky.SetColor("_GroundColor", new Color(0.42f, 0.44f, 0.46f));
            sky.SetFloat("_Exposure", 1.15f);
            EditorUtility.SetDirty(sky);

            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.0f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            RenderSettings.reflectionIntensity = 0.9f;
            var sun = Object.FindFirstObjectByType<Light>();
            if (sun != null)
            {
                RenderSettings.sun = sun;
                sun.shadows = LightShadows.Soft;
                sun.color = new Color(1f, 0.95f, 0.88f);
                sun.intensity = 1.35f;
            }

            var volume = new GameObject("PostProcessVolume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = Profile();

            var camera = Camera.main;
            if (camera != null)
            {
                camera.clearFlags = CameraClearFlags.Skybox;
                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
            }

            if (!Lightmapping.TryGetLightingSettings(out var lighting) || lighting == null)
            {
                lighting = new LightingSettings { name = "ShipyardTwinLighting" };
                Lightmapping.lightingSettings = lighting;
            }

            lighting.bakedGI = false;
            lighting.realtimeGI = false;
            Lightmapping.Bake(); // nothing is lightmapped: this bakes the ambient probe and the default reflection
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        }

        private static VolumeProfile Profile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile != null)
            {
                Configure(profile);
                return profile;
            }

            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);
            Configure(profile);
            return profile;
        }

        private static T Component<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet<T>(out var component))
            {
                component = profile.Add<T>(true);
                AssetDatabase.AddObjectToAsset(component, profile);
            }

            return component;
        }

        /// <summary>(Re)applies the grading every build so tuning here always reaches the scene.</summary>
        private static void Configure(VolumeProfile profile)
        {
            var tonemap = Component<Tonemapping>(profile);
            tonemap.mode.Override(TonemappingMode.ACES);
            var bloom = Component<Bloom>(profile);
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.25f);
            bloom.scatter.Override(0.6f);
            var grading = Component<ColorAdjustments>(profile);
            grading.postExposure.Override(0.25f);
            grading.contrast.Override(10f);
            grading.saturation.Override(-6f);
            var balance = Component<WhiteBalance>(profile);
            balance.temperature.Override(4f);
            var vignette = Component<Vignette>(profile);
            vignette.intensity.Override(0.16f);
            vignette.smoothness.Override(0.5f);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
