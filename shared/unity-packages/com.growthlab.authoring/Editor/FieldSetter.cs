using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>
    /// Sets serialized fields by name on a GameObject's components, the same way the Inspector does.
    /// <c>"EquipmentId"</c> matches a serialized field named EquipmentId, equipmentId, _equipmentId or
    /// m_EquipmentId on any component; <c>"EquipmentView.EquipmentId"</c> limits it to one component type.
    /// Supported values: text, numbers, true/false, enum names, "#RRGGBB" colours, "x;y;z" vectors and
    /// asset paths (relative to the spec folder) for object references.
    /// </summary>
    public static class FieldSetter
    {
        /// <summary>Applies "Field=value" entries. Problems are added to <paramref name="errors"/>.</summary>
        public static void ApplyAll(GameObject target, IEnumerable<string> entries, Func<string, string> resolvePath,
            List<string> errors, string context)
        {
            if (entries == null)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                var split = entry.IndexOf('=');
                if (split <= 0)
                {
                    errors.Add(context + ": '" + entry + "' must look like Field=value.");
                    continue;
                }

                Apply(target, entry.Substring(0, split).Trim(), entry.Substring(split + 1).Trim(), resolvePath, errors, context);
            }
        }

        public static bool Apply(GameObject target, string field, string value, Func<string, string> resolvePath,
            List<string> errors, string context)
        {
            string componentName = null;
            var dot = field.LastIndexOf('.');
            if (dot > 0)
            {
                componentName = field.Substring(0, dot);
                field = field.Substring(dot + 1);
            }

            foreach (var component in target.GetComponents<Component>())
            {
                if (component == null || (componentName != null && !Matches(component.GetType(), componentName)))
                {
                    continue;
                }

                var serialized = new SerializedObject(component);
                var property = Find(serialized, field);
                if (property == null)
                {
                    continue;
                }

                if (!Assign(property, value, resolvePath, out var problem))
                {
                    errors.Add(context + ": " + component.GetType().Name + "." + field + " = '" + value + "': " + problem);
                    return false;
                }

                serialized.ApplyModifiedPropertiesWithoutUndo();
                return true;
            }

            errors.Add(context + ": no serialized field '" + field + "'" +
                       (componentName != null ? " on " + componentName : " on any component") + " of '" + target.name +
                       "'. Run GrowthLab > Authoring > Write Catalog to list fields.");
            return false;
        }

        private static bool Matches(Type type, string name) =>
            string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase);

        private static SerializedProperty Find(SerializedObject serialized, string field)
        {
            var camel = char.ToLowerInvariant(field[0]) + field.Substring(1);
            foreach (var candidate in new[] { field, camel, "_" + camel, "m_" + field, "m_" + camel })
            {
                var property = serialized.FindProperty(candidate);
                if (property != null)
                {
                    return property;
                }
            }

            // Case-insensitive fallback over top-level properties.
            var iterator = serialized.GetIterator();
            var key = field.ToLowerInvariant();
            if (iterator.NextVisible(true))
            {
                do
                {
                    var name = iterator.name.TrimStart('_').ToLowerInvariant();
                    if (name.StartsWith("m_", StringComparison.Ordinal))
                    {
                        name = name.Substring(2);
                    }

                    if (name == key)
                    {
                        return iterator.Copy();
                    }
                }
                while (iterator.NextVisible(false));
            }

            return null;
        }

        private static bool Assign(SerializedProperty property, string value, Func<string, string> resolvePath, out string problem)
        {
            problem = null;
            var inv = CultureInfo.InvariantCulture;
            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    property.stringValue = value;
                    return true;
                case SerializedPropertyType.Integer:
                    if (long.TryParse(value, NumberStyles.Integer, inv, out var l))
                    {
                        property.longValue = l;
                        return true;
                    }

                    problem = "not an integer";
                    return false;
                case SerializedPropertyType.Float:
                    if (double.TryParse(value, NumberStyles.Float, inv, out var d))
                    {
                        property.doubleValue = d;
                        return true;
                    }

                    problem = "not a number";
                    return false;
                case SerializedPropertyType.Boolean:
                    if (bool.TryParse(value, out var b))
                    {
                        property.boolValue = b;
                        return true;
                    }

                    problem = "use true or false";
                    return false;
                case SerializedPropertyType.Enum:
                    var index = Array.FindIndex(property.enumNames, n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        property.enumValueIndex = index;
                        return true;
                    }

                    problem = "expected one of " + string.Join(", ", property.enumNames);
                    return false;
                case SerializedPropertyType.Color:
                    if (ColorUtility.TryParseHtmlString(value, out var color))
                    {
                        property.colorValue = color;
                        return true;
                    }

                    problem = "expected a colour like #3B82F6";
                    return false;
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                    if (TryVector(value, out var v))
                    {
                        if (property.propertyType == SerializedPropertyType.Vector2)
                        {
                            property.vector2Value = v;
                        }
                        else
                        {
                            property.vector3Value = v;
                        }

                        return true;
                    }

                    problem = "expected x;y;z";
                    return false;
                case SerializedPropertyType.ObjectReference:
                    var path = resolvePath(value);
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                    if (asset == null)
                    {
                        problem = "no asset at " + path;
                        return false;
                    }

                    property.objectReferenceValue = asset;
                    if (property.objectReferenceValue == null)
                    {
                        problem = "asset type " + asset.GetType().Name + " does not fit the field";
                        return false;
                    }

                    return true;
                default:
                    problem = "field type " + property.propertyType + " is not supported";
                    return false;
            }
        }

        public static bool TryVector(string value, out Vector3 vector)
        {
            vector = default;
            var parts = value.Split(';');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return false;
            }

            var inv = CultureInfo.InvariantCulture;
            var ok = float.TryParse(parts[0], NumberStyles.Float, inv, out vector.x) &
                     float.TryParse(parts[1], NumberStyles.Float, inv, out vector.y);
            if (parts.Length == 3)
            {
                ok &= float.TryParse(parts[2], NumberStyles.Float, inv, out vector.z);
            }

            return ok;
        }
    }
}
