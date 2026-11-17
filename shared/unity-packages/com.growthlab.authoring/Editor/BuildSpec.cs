using System;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>
    /// Root of a <c>*.build.json</c> file. Relative paths (placements, uxml, model, asset values) are resolved
    /// against the folder of the spec file. Output paths (<see cref="Scene"/>, <see cref="OutputFolder"/>)
    /// must start with <c>Assets/</c>. See docs/AUTHORING.md for the full reference.
    /// </summary>
    [Serializable]
    public sealed class BuildSpec
    {
        /// <summary>Scene to (re)create, e.g. "Assets/Plant/Plant.unity".</summary>
        public string Scene;

        /// <summary>Where prefabs, materials and UI settings go. Default: the scene's folder.</summary>
        public string OutputFolder;

        /// <summary>Put the scene first in Build Settings.</summary>
        public bool AddToBuildSettings = true;

        public CameraSpec Camera = new CameraSpec();

        public LightSpec Light = new LightSpec();

        public PrefabSpec[] Prefabs = new PrefabSpec[0];

        /// <summary>CSV file with one row per placed prefab instance (see <see cref="PlacementTable"/>).</summary>
        public string Placements;

        /// <summary>Plain scene objects such as a bootstrap or a manager.</summary>
        public ObjectSpec[] Objects = new ObjectSpec[0];

        public UiSpec[] Ui = new UiSpec[0];
    }

    [Serializable]
    public sealed class CameraSpec
    {
        public float[] Position = { 0f, 8f, -12f };
        public float[] LookAt = { 0f, 0f, 0f };

        /// <summary>Hex colour, e.g. "#1E2230". Empty keeps the skybox.</summary>
        public string Background = "#1E2230";
    }

    [Serializable]
    public sealed class LightSpec
    {
        public float[] Rotation = { 50f, -30f, 0f };
        public float Intensity = 1f;
    }

    [Serializable]
    public sealed class PrefabSpec
    {
        public string Name;

        /// <summary>Cube, Sphere, Capsule, Cylinder, Plane, Quad or Empty. Ignored when <see cref="Model"/> is set.</summary>
        public string Shape = "Cube";

        /// <summary>Optional model asset (FBX, prefab). A missing file falls back to <see cref="Shape"/> with a warning.</summary>
        public string Model;

        public float[] Scale;

        /// <summary>Hex colour for the primitive's material, e.g. "#3B82F6".</summary>
        public string Color;

        /// <summary>Component types to add, by class name or full name.</summary>
        public string[] Components = new string[0];

        /// <summary>Field values, "Field=value" or "Component.Field=value".</summary>
        public string[] Set = new string[0];
    }

    [Serializable]
    public sealed class ObjectSpec
    {
        public string Name;

        /// <summary>Optional prefab name from <see cref="BuildSpec.Prefabs"/>.</summary>
        public string Prefab;

        public float[] Position;
        public float[] Rotation;
        public string[] Components = new string[0];
        public string[] Set = new string[0];
    }

    [Serializable]
    public sealed class UiSpec
    {
        public string Name = "UI";

        /// <summary>UXML file for the UIDocument.</summary>
        public string Uxml;

        public int SortingOrder;
        public string[] Components = new string[0];
        public string[] Set = new string[0];
    }
}
