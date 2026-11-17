using System;
using System.IO;
using GrowthLab.Authoring.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ShipyardTwin.Editor
{
    /// <summary>
    /// Scene generation and macOS build. The scene comes from Authoring/shipyard.build.json + dock.csv +
    /// Dashboard.uxml (GrowthLab Authoring); edit those, not Generated/.
    /// Menu: ShipyardTwin. Batch: <c>-executeMethod ShipyardTwin.Editor.ShipyardTwinBuild.BuildMacBatch</c>
    /// (or <c>CreateSceneBatch</c>). Output: <c>../Builds/ShipyardTwin.app</c>.
    /// </summary>
    public static class ShipyardTwinBuild
    {
        public const string SpecPath = "Assets/ShipyardTwin/Authoring/shipyard.build.json";

        private static string BuildPath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Builds", "ShipyardTwin.app"));

        [MenuItem("ShipyardTwin/Create Scene")]
        public static void CreateScene()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            ShipyardTwinRendering.EnsurePipeline();
            var result = SpecBuilder.Build(SpecPath, openScene: true);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.ToString());
            }

            ShipyardTwinRendering.DecorateScene(result.Scene);
            Debug.Log(result.ToString());
        }

        [MenuItem("ShipyardTwin/Build macOS App")]
        public static void BuildMac()
        {
            EditorBuildSettings.scenes = new EditorBuildSettingsScene[0];
            CreateScene();
            PlayerSettings.productName = "ShipyardTwin";
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Standalone, "com.shipyardtwin.app");
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.enableFrameTimingStats = true;   // CPU/GPU frame times for PerfProbe (-shipyard-bench)

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = Array.ConvertAll(EditorBuildSettings.scenes, s => s.path),
                locationPathName = BuildPath,
                target = BuildTarget.StandaloneOSX,
                // SHIPYARD_DEV_BUILD=1: development player, so PerfProbe can sample profiler markers.
                options = System.Environment.GetEnvironmentVariable("SHIPYARD_DEV_BUILD") == "1" ? BuildOptions.Development : BuildOptions.None,
            });
            var summary = report.summary;
            Debug.Log($"[ShipyardTwin] build: {summary.result}, {summary.totalErrors} errors, {summary.outputPath}");
            if (summary.result != BuildResult.Succeeded)
            {
                throw new Exception("ShipyardTwin build " + summary.result);
            }
        }

        public static void CreateSceneBatch() => RunBatch(CreateScene);

        public static void BuildMacBatch() => RunBatch(BuildMac);

        private static void RunBatch(Action action)
        {
            try
            {
                action();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
