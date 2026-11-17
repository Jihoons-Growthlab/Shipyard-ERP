using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace ShipyardTwin
{
    /// <summary>
    /// Benchmark run for comparing builds: <c>-shipyard-bench &lt;out.json&gt; [-shipyard-bench-switches N]</c>.
    /// Waits until the scene reports ready (<see cref="MarkReady"/>), then measures an idle phase (camera and playback
    /// still), a playing phase, optional hull switches, and writes startup time, frame timings (CPU main/render, GPU via
    /// FrameTimingManager), memory and object counts as JSON, then quits. Views register how to play and switch.
    /// Without the argument it does nothing.
    /// </summary>
    public sealed class PerfProbe : MonoBehaviour
    {
        public const string BenchArgument = "-shipyard-bench";
        public const string SwitchArgument = "-shipyard-bench-switches";
        public const string PhaseArgument = "-shipyard-bench-seconds";

        private static float _readyAt = -1f;

        /// <summary>Starts playback for the playing phase (set by the scene's playback owner).</summary>
        public static Action StartPlayback;

        public static Action StopPlayback;

        /// <summary>Selects hull number i (modulo the hull count) for the switch phase; null when not supported.</summary>
        public static Action<int> SwitchHull;

        /// <summary>True while the scene is still loading after a selection (meshes); the playing phase waits for it.</summary>
        public static Func<bool> Busy;

        /// <summary>Extra numbers a view wants in the report (block counts, active hull...).</summary>
        public static Func<Dictionary<string, double>> Extra;

        public static float ReadyAt => _readyAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _readyAt = -1f;
            StartPlayback = StopPlayback = null;
            Busy = null;
            SwitchHull = null;
            Extra = null;
        }

        /// <summary>Called once when blocks are placed and the scene is usable.</summary>
        public static void MarkReady()
        {
            if (_readyAt < 0f)
            {
                _readyAt = Time.realtimeSinceStartup;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Argument(BenchArgument) != null && FindFirstObjectByType<PerfProbe>() == null)
            {
                new GameObject("PerfProbe").AddComponent<PerfProbe>();
            }
        }

        private IEnumerator Start()
        {
            var output = Argument(BenchArgument);
            var seconds = float.TryParse(Argument(PhaseArgument), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 8f;
            var switches = int.TryParse(Argument(SwitchArgument), out var n) ? n : 0;
            var report = new StringBuilder("{\n");
            var timeout = Time.realtimeSinceStartup + 120f;
            while (_readyAt < 0f && Time.realtimeSinceStartup < timeout)
            {
                yield return null;
            }

            Field(report, "ready_s", _readyAt);
            Field(report, "first_frame_after_ready_s", Time.realtimeSinceStartup);
            yield return new WaitForSecondsRealtime(2f);          // let shaders, shadows and streaming settle
            Counts(report, "idle_start");

            var idle = new Sampler();
            var markers = Debug.isDebugBuild ? new Markers() : null;
            yield return idle.Run(seconds, markers);
            idle.Write(report, "idle");
            markers?.Write(report, "idle");
            Memory(report, "idle");

            if (StartPlayback != null)
            {
                // Selecting a hull loads its meshes: time that separately, then measure playback alone.
                var selected = Time.realtimeSinceStartup;
                StartPlayback();
                StopPlayback?.Invoke();
                var load = new Sampler();
                var loading = load.RunWhile(() => Busy != null && Busy() && Time.realtimeSinceStartup < selected + 30f);
                yield return loading;
                Field(report, "select_load_s", Time.realtimeSinceStartup - selected);
                load.Write(report, "select_load");
                StartPlayback();
                var play = new Sampler();
                var playMarkers = Debug.isDebugBuild ? new Markers() : null;
                yield return play.Run(seconds, playMarkers);
                play.Write(report, "playing");
                playMarkers?.Write(report, "playing");
                Memory(report, "playing");
                StopPlayback?.Invoke();
            }

            if (SwitchHull != null && switches > 0)
            {
                var objects = new List<int>();
                var used = new List<double>();
                for (var i = 0; i < switches; i++)
                {
                    SwitchHull(i);
                    for (var f = 0; f < 30; f++)
                    {
                        yield return null;
                    }

                    StartPlayback?.Invoke();
                    yield return new WaitForSecondsRealtime(0.5f);
                    StopPlayback?.Invoke();
                    yield return Resources.UnloadUnusedAssets();
                    objects.Add(FindObjectsByType<Transform>(FindObjectsSortMode.None).Length);
                    used.Add(Profiler.GetTotalAllocatedMemoryLong() / 1048576.0);
                }

                report.Append("  \"switch_transforms\": [").Append(string.Join(", ", objects)).Append("],\n");
                report.Append("  \"switch_allocated_mb\": [")
                    .Append(string.Join(", ", used.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))).Append("],\n");
                Counts(report, "after_switches");
            }

            if (Extra != null)
            {
                foreach (var pair in Extra())
                {
                    Field(report, "extra_" + pair.Key, pair.Value);
                }
            }

            report.Append("  \"screen\": \"").Append(Screen.width).Append('x').Append(Screen.height).Append("\"\n}\n");
            File.WriteAllText(output, report.ToString());
            Application.Quit();
        }

        private static void Counts(StringBuilder report, string prefix)
        {
            var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
            Field(report, prefix + "_transforms", FindObjectsByType<Transform>(FindObjectsSortMode.None).Length);
            Field(report, prefix + "_renderers", FindObjectsByType<Renderer>(FindObjectsSortMode.None).Length);
            Field(report, prefix + "_colliders", FindObjectsByType<Collider>(FindObjectsSortMode.None).Length);
            Field(report, prefix + "_meshes", meshes.Length);
            Field(report, prefix + "_mesh_vertices_k", meshes.Sum(m => (double)m.vertexCount) / 1000.0);
            Field(report, prefix + "_materials", Resources.FindObjectsOfTypeAll<Material>().Length);
            // Enabled renderers per top-level object: where draw calls come from.
            foreach (var group in FindObjectsByType<Renderer>(FindObjectsSortMode.None).Where(r => r.enabled)
                         .GroupBy(r => r.transform.root.name).OrderByDescending(g => g.Count()).Take(12))
            {
                Field(report, prefix + "_drawn_" + group.Key.Replace(' ', '_').Replace('"', '_'), group.Count());
            }
        }

        private static void Memory(StringBuilder report, string prefix)
        {
            Field(report, prefix + "_allocated_mb", Profiler.GetTotalAllocatedMemoryLong() / 1048576.0);
            Field(report, prefix + "_reserved_mb", Profiler.GetTotalReservedMemoryLong() / 1048576.0);
            Field(report, prefix + "_mono_used_mb", Profiler.GetMonoUsedSizeLong() / 1048576.0);
            foreach (var (name, key) in new[] { ("Total Used Memory", "total_used"), ("Gfx Used Memory", "gfx_used"),
                         ("System Used Memory", "system_used") })
            {
                using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, name);
                if (recorder.Valid && recorder.CurrentValue > 0)
                {
                    Field(report, prefix + "_" + key + "_mb", recorder.CurrentValue / 1048576.0);
                }
            }
        }

        private static void Field(StringBuilder report, string key, double value) =>
            report.Append("  \"").Append(key).Append("\": ").Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append(",\n");

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        /// <summary>Average ms per frame of selected profiler markers (development builds only).</summary>
        private sealed class Markers : IDisposable
        {
            private static readonly string[] Names =
            {
                "PlayerLoop", "Update.ScriptRunBehaviourUpdate", "PreLateUpdate.ScriptRunBehaviourLateUpdate",
                "UIElementsRuntimeUtility.UpdateRuntimePanels", "UIElementsRuntimeUtility.RepaintPanels",
                "Physics.SyncColliderTransform", "Physics.Raycast", "Physics.SyncTransforms",
                "PostLateUpdate.FinishFrameRendering", "Inl_UniversalRenderPipeline.RenderSingleCameraInternal",
                "Inl_MainLightShadow", "Inl_ScreenSpaceAmbientOcclusion", "Inl_DrawOpaqueObjects", "Inl_UberPostProcess",
                "RenderLoop.ScheduleDraw", "Shadows.RenderJob", "Culling", "Gfx.WaitForPresentOnGfxThread",
                "Gfx.WaitForGfxCommandsFromMainThread", "WaitForTargetFPS", "GC.Alloc", "GC.Collect",
            };

            private readonly List<(string Name, ProfilerRecorder Recorder, double Sum)> _recorders =
                new List<(string, ProfilerRecorder, double)>();
            private int _frames;

            public Markers()
            {
                foreach (var name in Names)
                {
                    var recorder = ProfilerRecorder.StartNew(new ProfilerCategory("Any"), name, 1);
                    if (!recorder.Valid)
                    {
                        recorder.Dispose();
                        recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, name, 1);
                    }

                    _recorders.Add((name, recorder, 0));
                }
            }

            public void Sample()
            {
                _frames++;
                for (var i = 0; i < _recorders.Count; i++)
                {
                    var r = _recorders[i];
                    if (r.Recorder.Valid)
                    {
                        _recorders[i] = (r.Name, r.Recorder, r.Sum + r.Recorder.LastValue);
                    }
                }
            }

            public void Write(StringBuilder report, string prefix)
            {
                foreach (var (name, recorder, sum) in _recorders)
                {
                    if (recorder.Valid && sum > 0 && _frames > 0)
                    {
                        // Timing markers are in ns; GC.Alloc counts bytes.
                        var value = name == "GC.Alloc" ? sum / _frames / 1024.0 : sum / _frames / 1e6;
                        Field(report, prefix + "_marker_" + name.Replace('.', '_') + (name == "GC.Alloc" ? "_kb" : "_ms"), value);
                    }
                }

                Dispose();
            }

            public void Dispose()
            {
                foreach (var r in _recorders)
                {
                    r.Recorder.Dispose();
                }
            }
        }

        /// <summary>Per-frame interval and FrameTimingManager CPU/GPU times over a phase.</summary>
        private sealed class Sampler
        {
            private readonly List<double> _interval = new List<double>(), _main = new List<double>(),
                _render = new List<double>(), _gpu = new List<double>();
            private readonly FrameTiming[] _timing = new FrameTiming[1];

            public IEnumerator RunWhile(Func<bool> condition)
            {
                while (condition())
                {
                    yield return null;
                    _interval.Add(Time.unscaledDeltaTime * 1000.0);
                }
            }

            public IEnumerator Run(float seconds, Markers markers = null)
            {
                var end = Time.realtimeSinceStartup + seconds;
                while (Time.realtimeSinceStartup < end)
                {
                    yield return null;
                    markers?.Sample();
                    _interval.Add(Time.unscaledDeltaTime * 1000.0);
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
                    {
                        if (_timing[0].cpuMainThreadFrameTime > 0) _main.Add(_timing[0].cpuMainThreadFrameTime);
                        if (_timing[0].cpuRenderThreadFrameTime > 0) _render.Add(_timing[0].cpuRenderThreadFrameTime);
                        if (_timing[0].gpuFrameTime > 0) _gpu.Add(_timing[0].gpuFrameTime);
                    }
                }
            }

            public void Write(StringBuilder report, string prefix)
            {
                Stats(report, prefix + "_frame_ms", _interval);
                Stats(report, prefix + "_cpu_main_ms", _main);
                Stats(report, prefix + "_cpu_render_ms", _render);
                Stats(report, prefix + "_gpu_ms", _gpu);
            }

            private static void Stats(StringBuilder report, string key, List<double> values)
            {
                if (values.Count == 0)
                {
                    return;
                }

                var sorted = values.OrderBy(v => v).ToList();
                Field(report, key + "_avg", sorted.Average());
                Field(report, key + "_p95", sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))]);
                Field(report, key + "_n", sorted.Count);
            }
        }
    }
}
