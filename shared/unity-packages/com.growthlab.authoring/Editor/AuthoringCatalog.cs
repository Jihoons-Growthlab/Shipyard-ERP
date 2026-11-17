using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>
    /// Writes a short Markdown list of what specs can use: project components with their settable fields,
    /// UI Toolkit custom elements with their UXML attributes, and the specs in the project. Reading this one
    /// file is much cheaper than reading the scripts. Menu: GrowthLab > Authoring > Write Catalog.
    /// Batch: <c>-executeMethod GrowthLab.Authoring.Editor.AuthoringCatalog.WriteBatch [-out &lt;file&gt;]</c>.
    /// </summary>
    public static class AuthoringCatalog
    {
        public const string DefaultFile = "GrowthLabCatalog.md";

        private static readonly string[] SkippedAssemblyPrefixes =
            { "Unity", "UnityEngine", "UnityEditor", "System", "mscorlib", "netstandard", "Mono.", "nunit", "Bee.", "PlayerBuildProgram" };

        [MenuItem("GrowthLab/Authoring/Write Catalog")]
        public static void WriteMenu()
        {
            var path = Write(DefaultFile);
            Debug.Log("[Authoring] Catalog written: " + path);
            EditorUtility.RevealInFinder(path);
        }

        public static void WriteBatch()
        {
            var outPath = DefaultFile;
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-out")
                {
                    outPath = args[i + 1];
                }
            }

            Debug.Log("[Authoring] Catalog written: " + Write(outPath));
            EditorApplication.Exit(0);
        }

        /// <returns>Full path of the written file (relative paths are under the project folder).</returns>
        public static string Write(string path)
        {
            var full = Path.IsPathRooted(path) ? path : Path.Combine(Directory.GetParent(Application.dataPath).FullName, path);
            File.WriteAllText(full, Build());
            return full;
        }

        public static string Build()
        {
            var text = new StringBuilder();
            text.AppendLine("# GrowthLab authoring catalog");
            text.AppendLine();
            text.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ". Regenerate after adding scripts: GrowthLab > Authoring > Write Catalog.");
            text.AppendLine();

            text.AppendLine("## Specs");
            foreach (var spec in SpecBuilder.FindSpecs())
            {
                text.AppendLine("- " + spec);
            }

            text.AppendLine();
            text.AppendLine("## Components (use in Components; fields in Set / CSV columns)");
            foreach (var type in TypeCache.GetTypesDerivedFrom<MonoBehaviour>().Where(IsProjectType).OrderBy(t => t.FullName))
            {
                var fields = SerializedFields(type).ToList();
                text.Append("- `").Append(type.FullName).Append('`');
                if (fields.Count > 0)
                {
                    text.Append(": ").Append(string.Join(", ", fields));
                }

                text.AppendLine();
            }

            text.AppendLine();
            text.AppendLine("## UI elements (UXML: xmlns:gl=\"<namespace>\" then <gl:Name attr=\"\" />)");
            foreach (var type in TypeCache.GetTypesWithAttribute<UxmlElementAttribute>().Where(IsProjectType).OrderBy(t => t.FullName))
            {
                var attributes = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(p => p.GetCustomAttribute<UxmlAttributeAttribute>() != null)
                    .Select(p => Kebab(p.Name) + ":" + TypeName(p.PropertyType));
                text.Append("- `").Append(type.FullName).Append("` ").AppendLine(string.Join(", ", attributes));
            }

            return text.ToString();
        }

        private static bool IsProjectType(Type type)
        {
            if (type.IsAbstract || type.IsGenericType)
            {
                return false;
            }

            var assembly = type.Assembly.GetName().Name;
            return !SkippedAssemblyPrefixes.Any(p => assembly.StartsWith(p, StringComparison.Ordinal)) &&
                   !assembly.EndsWith(".Tests.Editor", StringComparison.Ordinal) && !assembly.EndsWith(".Tests.Runtime", StringComparison.Ordinal);
        }

        private static IEnumerable<string> SerializedFields(Type type)
        {
            for (var t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var serialized = (field.IsPublic && !field.IsInitOnly && field.GetCustomAttribute<NonSerializedAttribute>() == null) ||
                                     field.GetCustomAttribute<SerializeField>() != null;
                    if (!serialized)
                    {
                        continue;
                    }

                    var name = field.Name.TrimStart('_');
                    if (name.StartsWith("m_", StringComparison.Ordinal))
                    {
                        name = name.Substring(2);
                    }

                    yield return char.ToUpperInvariant(name[0]) + name.Substring(1) + ":" + TypeName(field.FieldType);
                }
            }
        }

        private static string TypeName(Type type)
        {
            if (type == typeof(float) || type == typeof(double))
            {
                return "number";
            }

            if (type == typeof(int) || type == typeof(long))
            {
                return "int";
            }

            if (type == typeof(bool))
            {
                return "bool";
            }

            if (type == typeof(string))
            {
                return "text";
            }

            if (type.IsEnum)
            {
                return string.Join("|", Enum.GetNames(type));
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                return "asset(" + type.Name + ")";
            }

            return type.Name;
        }

        private static string Kebab(string name)
        {
            var text = new StringBuilder();
            for (var i = 0; i < name.Length; i++)
            {
                if (char.IsUpper(name[i]) && i > 0)
                {
                    text.Append('-');
                }

                text.Append(char.ToLowerInvariant(name[i]));
            }

            return text.ToString();
        }
    }
}
