using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>Finds component types by short or full name across all loaded assemblies.</summary>
    public static class TypeLookup
    {
        public static Type Component(string name, out string problem)
        {
            problem = null;
            var all = TypeCache.GetTypesDerivedFrom<Component>();
            var exact = all.Where(t => t.FullName == name).ToList();
            if (exact.Count == 1)
            {
                return exact[0];
            }

            var byName = all.Where(t => t.Name == name && !t.IsAbstract).ToList();
            if (byName.Count == 1)
            {
                return byName[0];
            }

            if (byName.Count > 1)
            {
                problem = "component '" + name + "' is ambiguous: " + string.Join(", ", byName.Select(t => t.FullName)) +
                          ". Use the full name.";
                return null;
            }

            var similar = all.Where(t => !t.IsAbstract && t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .Take(5).Select(t => t.FullName).ToList();
            problem = "unknown component '" + name + "'" + (similar.Count > 0 ? ". Similar: " + string.Join(", ", similar) : string.Empty);
            return null;
        }

        /// <summary>Adds each component (if missing). Problems go to <paramref name="errors"/>.</summary>
        public static void AddComponents(GameObject target, IEnumerable<string> names, List<string> errors, string context)
        {
            if (names == null)
            {
                return;
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var type = Component(name.Trim(), out var problem);
                if (type == null)
                {
                    errors.Add(context + ": " + problem);
                    continue;
                }

                if (target.GetComponent(type) == null && target.AddComponent(type) == null)
                {
                    errors.Add(context + ": could not add " + type.FullName + " (editor-only or abstract script?).");
                }
            }
        }
    }
}
