using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>Menu: GrowthLab > Authoring > New Spec. Creates a working starter folder you then edit.</summary>
    public static class SpecTemplates
    {
        [MenuItem("GrowthLab/Authoring/New Spec")]
        public static void CreateMenu()
        {
            var folder = Create("Assets/NewTwin");
            var spec = folder + "/" + Path.GetFileName(folder) + SpecBuilder.SpecSuffix;
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<TextAsset>(spec);
            EditorGUIUtility.PingObject(Selection.activeObject);
            Debug.Log("[Authoring] Created " + folder + ". Edit the .build.json / placements.csv / Dashboard.uxml, then right-click the spec > GrowthLab > Build From Spec.");
        }

        /// <summary>Batch: <c>-executeMethod GrowthLab.Authoring.Editor.SpecTemplates.CreateBatch [-folder Assets/MyTwin]</c>.</summary>
        public static void CreateBatch()
        {
            var folder = "Assets/NewTwin";
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-folder")
                {
                    folder = args[i + 1];
                }
            }

            Debug.Log("[Authoring] Created " + Create(folder));
            EditorApplication.Exit(0);
        }

        /// <summary>Creates spec + placements + UXML in a new unique folder. Returns the folder path.</summary>
        public static string Create(string desiredFolder)
        {
            var folder = AssetDatabase.GenerateUniqueAssetPath(desiredFolder);
            SpecBuilder.EnsureFolder(folder);
            var name = Path.GetFileName(folder);
            var hasGrowthLabUi = Type.GetType("GrowthLab.UI.Gauge, GrowthLab.UI") != null;

            Write(folder + "/" + name + SpecBuilder.SpecSuffix, @"{
  ""Scene"": """ + folder + "/" + name + @".unity"",
  ""Camera"": { ""Position"": [0, 7, -11], ""LookAt"": [0, 0, 2], ""Background"": ""#1E2230"" },
  ""Prefabs"": [
    { ""Name"": ""Box"",  ""Shape"": ""Cube"",     ""Color"": ""#3B82F6"" },
    { ""Name"": ""Pipe"", ""Shape"": ""Cylinder"", ""Color"": ""#94A3B8"", ""Scale"": [0.6, 1.5, 0.6] }
  ],
  ""Placements"": ""placements.csv"",
  ""Ui"": [
    { ""Name"": ""Dashboard"", ""Uxml"": ""Dashboard.uxml"" }
  ]
}
");
            Write(folder + "/placements.csv", @"name,prefab,x,y,z,rotY
# one row per object. Extra columns set component fields (e.g. EquipmentId).
box-01,Box,-2,0.5,0,0
box-02,Box,0,0.5,0,0
pipe-01,Pipe,2,1.5,0,0
");
            Write(folder + "/Dashboard.uxml", hasGrowthLabUi
                ? @"<ui:UXML xmlns:ui=""UnityEngine.UIElements"" xmlns:gl=""GrowthLab.UI"">
    <ui:VisualElement class=""gl-screen"">
        <ui:VisualElement class=""gl-panel gl-sidebar"">
            <ui:Label text=""" + name + @""" class=""gl-title"" />
            <ui:Label text=""Edit Dashboard.uxml to change this panel."" class=""gl-subtitle"" />
            <gl:StatCard title=""box-01"" level=""Normal"">
                <gl:Gauge label=""Load"" unit=""%"" min=""0"" max=""100"" value=""42"" warning-at=""75"" alarm-at=""90"" />
            </gl:StatCard>
            <gl:MessageList name=""events"" empty-text=""No events yet"" class=""gl-grow"" />
        </ui:VisualElement>
    </ui:VisualElement>
</ui:UXML>
"
                : @"<ui:UXML xmlns:ui=""UnityEngine.UIElements"">
    <ui:Label text=""" + name + @""" style=""font-size: 18px; color: white; margin: 12px;"" />
</ui:UXML>
");
            AssetDatabase.Refresh();
            return folder;
        }

        private static void Write(string assetPath, string text) => File.WriteAllText(assetPath, text.Replace("\r\n", "\n"));
    }
}
