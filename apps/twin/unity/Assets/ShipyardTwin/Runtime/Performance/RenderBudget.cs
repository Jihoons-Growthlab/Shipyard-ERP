using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ShipyardTwin
{
    /// <summary>
    /// Rendering cost settings (ShipyardTwinSettings, overridable on the command line):
    /// <c>-shipyard-fps N</c> caps the frame rate (vSync off), <c>-shipyard-shadows high|medium|low|off</c> sets the
    /// main-light shadow distance, cascades and map size, <c>-shipyard-idle-render N</c> renders only every Nth frame while
    /// the scene is still (no input for 0.5 s, camera settled, playback paused, nothing on the hook); scripts and input
    /// keep running every frame, so the first input renders at full rate again.
    /// The URP asset is cloned before changing it, so the project asset is never modified.
    /// </summary>
    public sealed class RenderBudget : MonoBehaviour
    {
        public const string FpsArgument = "-shipyard-fps";
        public const string ShadowsArgument = "-shipyard-shadows";
        public const string IdleArgument = "-shipyard-idle-render";

        private const float IdleAfter = 0.5f;

        private int _idleInterval = 1;
        private float _lastActive;
        private Vector3 _lastMouse;
        private OrbitCameraRig _rig;
        private ShipView _ship;

        public static string ShadowPreset { get; private set; } = "high";
        public static int FrameCap { get; private set; }
        public static int IdleInterval { get; private set; } = 1;

        /// <summary>True while rendering is reduced.</summary>
        public bool Idle { get; private set; }

        public static void Apply(ShipyardTwinSettings settings)
        {
            FrameCap = Int(Argument(FpsArgument)) ?? settings.TargetFps;
            ShadowPreset = (Argument(ShadowsArgument) ?? settings.Shadows ?? "high").ToLowerInvariant();
            IdleInterval = Mathf.Max(1, Int(Argument(IdleArgument)) ?? settings.IdleRenderInterval);
            if (FrameCap > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = FrameCap;
            }

            ApplyShadows(ShadowPreset);
            var go = new GameObject("RenderBudget");
            go.AddComponent<RenderBudget>()._idleInterval = IdleInterval;
        }

        private static void ApplyShadows(string preset)
        {
            if (preset == "high" || !(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset))
            {
                return;
            }

            var clone = Instantiate(asset);
            clone.name = asset.name + " (" + preset + ")";
            switch (preset)
            {
                case "medium":
                    clone.shadowDistance = 700f;
                    clone.shadowCascadeCount = 2;
                    clone.mainLightShadowmapResolution = 2048;
                    break;
                case "low":
                    clone.shadowDistance = 400f;
                    clone.shadowCascadeCount = 1;
                    clone.mainLightShadowmapResolution = 1024;
                    break;
                case "off":
                    clone.shadowDistance = 0f;
                    break;
            }

            GraphicsSettings.defaultRenderPipeline = clone;
            QualitySettings.renderPipeline = clone;
        }

        private void Update()
        {
            if (_idleInterval <= 1)
            {
                return;
            }

            if (_rig == null) _rig = FindFirstObjectByType<OrbitCameraRig>();
            if (_ship == null) _ship = FindFirstObjectByType<ShipView>();
            var mouse = Input.mousePosition;
            var active = Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) ||
                         Math.Abs(Input.mouseScrollDelta.y) > 0.001f || (mouse - _lastMouse).sqrMagnitude > 0.25f ||
                         (_rig != null && !_rig.Settled) ||
                         (_ship != null && (_ship.Playback?.Playing == true || _ship.CurrentLift != null || !_ship.Ready));
            _lastMouse = mouse;
            if (active)
            {
                _lastActive = Time.unscaledTime;
            }

            Idle = Time.unscaledTime - _lastActive > IdleAfter;
            OnDemandRendering.renderFrameInterval = Idle ? _idleInterval : 1;
        }

        private static int? Int(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
