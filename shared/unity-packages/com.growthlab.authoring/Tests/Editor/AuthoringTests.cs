using System.Collections.Generic;
using System.IO;
using System.Linq;
using GrowthLab.Authoring.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.Authoring.Tests
{
    public sealed class AuthoringTests
    {
        private const string Root = "Assets/__GrowthLabAuthoringTest";
        private string _folder;

        [TearDown]
        public void TearDown()
        {
            if (_folder != null)
            {
                var scenePrefix = _folder + "/";
                EditorBuildSettings.scenes = EditorBuildSettings.scenes.Where(s => !s.path.StartsWith(scenePrefix)).ToArray();
                AssetDatabase.DeleteAsset(_folder);
                _folder = null;
            }
        }

        private string NewFolder()
        {
            _folder = AssetDatabase.GenerateUniqueAssetPath(Root);
            SpecBuilder.EnsureFolder(_folder);
            return _folder;
        }

        private static void Write(string path, string text)
        {
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path);
        }

        [Test]
        public void StarterTemplate_BuildsPrefabsSceneAndUi()
        {
            _folder = SpecTemplates.Create(Root);
            var spec = _folder + "/" + Path.GetFileName(_folder) + SpecBuilder.SpecSuffix;

            var result = SpecBuilder.Build(spec);

            Assert.IsTrue(result.Succeeded, result.ToString());
            Assert.AreEqual(2, result.Prefabs.Count);
            Assert.AreEqual(3, result.Instances);
            Assert.AreEqual(1, result.Documents);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(_folder + "/Prefabs/Box.prefab"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(_folder + "/Materials/Box.mat"));
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(_folder + "/UI/PanelSettings.asset");
            Assert.IsNotNull(panel);
            Assert.IsNotNull(panel.themeStyleSheet, "Theme assigned.");
            Assert.IsTrue(File.Exists(result.Scene));
            Assert.AreEqual(result.Scene, EditorBuildSettings.scenes[0].path);

            // Running again regenerates without errors (idempotent).
            Assert.IsTrue(SpecBuilder.Build(spec).Succeeded);
        }

        [Test]
        public void FieldsFromSetAndCsvColumns_AreApplied()
        {
            var folder = NewFolder();
            Write(folder + "/t.build.json", @"{
  ""Scene"": """ + folder + @"/T.unity"",
  ""Prefabs"": [ { ""Name"": ""Sensor"", ""Shape"": ""Cube"", ""Components"": [""Light""], ""Set"": [""Intensity=2.5"", ""BoxCollider.IsTrigger=true""] } ],
  ""Placements"": ""p.csv""
}");
            Write(folder + "/p.csv", "name,prefab,x,y,z,rotY,Light.Color,Size\ns-1,Sensor,1,2,3,90,#FF0000,1;2;3\n");

            var result = SpecBuilder.Build(folder + "/t.build.json");

            Assert.IsTrue(result.Succeeded, result.ToString());
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Prefabs/Sensor.prefab");
            Assert.AreEqual(2.5f, prefab.GetComponent<Light>().intensity, 1e-4);
            Assert.IsTrue(prefab.GetComponent<BoxCollider>().isTrigger);
        }

        [Test]
        public void Problems_AreReportedInOneReadableResult()
        {
            var folder = NewFolder();
            Write(folder + "/bad.build.json", @"{
  ""Scene"": """ + folder + @"/Bad.unity"",
  ""Prefabs"": [ { ""Name"": ""A"", ""Shape"": ""Cube"", ""Components"": [""NoSuchComponent""], ""Set"": [""NoSuchField=1""] } ],
  ""Placements"": ""p.csv"",
  ""Ui"": [ { ""Uxml"": ""missing.uxml"" } ]
}");
            Write(folder + "/p.csv", "name,prefab,x,y,z\na-1,A,0,0,0\nb-1,Missing,0,0,0\nc-1,A,zero,0,0\n");

            var result = SpecBuilder.Build(folder + "/bad.build.json");
            var text = result.ToString();

            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains("unknown component 'NoSuchComponent'", text);
            StringAssert.Contains("no serialized field 'NoSuchField'", text);
            StringAssert.Contains("unknown prefab 'Missing'", text);
            StringAssert.Contains("is not a number: 'zero'", text);
            StringAssert.Contains("no UXML at " + folder + "/missing.uxml", text);
            Assert.AreEqual(1, result.Instances, "Valid rows are still placed.");
        }

        [Test]
        public void InvalidJsonAndScenePath_AreRejected()
        {
            var folder = NewFolder();
            Write(folder + "/x.build.json", "{ not json");
            StringAssert.Contains("not valid JSON", SpecBuilder.Build(folder + "/x.build.json").ToString());

            Write(folder + "/y.build.json", "{ \"Scene\": \"Scenes/Plant.unity\" }");
            StringAssert.Contains("Scene must be an Assets/", SpecBuilder.Build(folder + "/y.build.json").ToString());
        }

        [Test]
        public void PlacementTable_ParsesRows_AndReportsBadLines()
        {
            var table = PlacementTable.Parse("# comment\nName,Prefab,X,Y,Z,RotY,EquipmentId\n\np1,Pump,1.5,0,-2,45,pump-01\nbad,Pump,1,2\nx,Pump,1,2,3,,a,extra\n");

            Assert.AreEqual(1, table.Rows.Count, "Only the valid row is kept.");
            var row = table.Rows[0];
            Assert.AreEqual("p1", row.Name);
            Assert.AreEqual(1.5f, row.X);
            Assert.AreEqual(45f, row.RotY);
            Assert.AreEqual(new KeyValuePair<string, string>("EquipmentId", "pump-01"), row.Fields.Single());
            Assert.AreEqual(2, table.Errors.Count, string.Join("\n", table.Errors));
            StringAssert.Contains("column 'Z' is not a number", table.Errors[0]);
            StringAssert.Contains("cannot contain commas", table.Errors[1]);

            Assert.IsNotEmpty(PlacementTable.Parse("id,x,y,z\n").Errors, "Missing required columns.");
        }

        [Test]
        public void ResolvePath_IsRelativeToSpecFolder()
        {
            Assert.AreEqual("Packages/p/A/x.uxml", SpecBuilder.ResolvePath("Packages/p/A", "x.uxml"));
            Assert.AreEqual("Packages/p/B/y.csv", SpecBuilder.ResolvePath("Packages/p/A", "../B/y.csv"));
            Assert.AreEqual("Assets/z.uxml", SpecBuilder.ResolvePath("Packages/p/A", "Assets/z.uxml"));
        }

        [Test]
        public void Catalog_ListsUiElementsAndSpecs()
        {
            var text = AuthoringCatalog.Build();
            StringAssert.Contains("## Components", text);
            StringAssert.Contains("## UI elements", text);
        }
    }
}
