using System;
using System.Collections.Generic;
using System.Globalization;

namespace GrowthLab.Authoring.Editor
{
    /// <summary>
    /// CSV with a header row. Required columns: <c>name,prefab,x,y,z</c>. Optional: <c>rotY</c>.
    /// Every other column is a field set on the instance (same rules as <c>Set</c> entries), e.g.
    /// <c>EquipmentId</c> or <c>EquipmentView.EquipmentId</c>. Empty cells are skipped. Lines starting
    /// with <c>#</c> and blank lines are ignored. Values cannot contain commas.
    /// </summary>
    public sealed class PlacementTable
    {
        private static readonly string[] Required = { "name", "prefab", "x", "y", "z" };

        public sealed class Row
        {
            public int Line;
            public string Name;
            public string Prefab;
            public float X;
            public float Y;
            public float Z;
            public float RotY;
            public readonly List<KeyValuePair<string, string>> Fields = new List<KeyValuePair<string, string>>();
        }

        public readonly List<Row> Rows = new List<Row>();
        public readonly List<string> Errors = new List<string>();

        public static PlacementTable Parse(string text, string source = "placements")
        {
            var table = new PlacementTable();
            string[] header = null;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var cells = line.Split(',');
                for (var c = 0; c < cells.Length; c++)
                {
                    cells[c] = cells[c].Trim();
                }

                if (header == null)
                {
                    header = cells;
                    foreach (var column in Required)
                    {
                        if (Array.FindIndex(header, h => string.Equals(h, column, StringComparison.OrdinalIgnoreCase)) < 0)
                        {
                            table.Errors.Add(source + ": header is missing column '" + column + "'. Header must start with name,prefab,x,y,z.");
                        }
                    }

                    if (table.Errors.Count > 0)
                    {
                        return table;
                    }

                    continue;
                }

                var row = new Row { Line = i + 1 };
                var ok = true;
                for (var c = 0; c < header.Length; c++)
                {
                    var value = c < cells.Length ? cells[c] : string.Empty;
                    var column = header[c];
                    switch (column.ToLowerInvariant())
                    {
                        case "name":
                            row.Name = value;
                            break;
                        case "prefab":
                            row.Prefab = value;
                            break;
                        case "x":
                            ok &= Number(value, out row.X, source, row.Line, column, table);
                            break;
                        case "y":
                            ok &= Number(value, out row.Y, source, row.Line, column, table);
                            break;
                        case "z":
                            ok &= Number(value, out row.Z, source, row.Line, column, table);
                            break;
                        case "roty":
                            ok &= value.Length == 0 || Number(value, out row.RotY, source, row.Line, column, table);
                            break;
                        default:
                            if (value.Length > 0)
                            {
                                row.Fields.Add(new KeyValuePair<string, string>(column, value));
                            }

                            break;
                    }
                }

                if (string.IsNullOrEmpty(row.Name) || string.IsNullOrEmpty(row.Prefab))
                {
                    table.Errors.Add(source + ":" + row.Line + ": name and prefab are required.");
                    ok = false;
                }

                if (cells.Length > header.Length)
                {
                    table.Errors.Add(source + ":" + row.Line + ": " + cells.Length + " cells but the header has " + header.Length +
                                     " (values cannot contain commas).");
                    ok = false;
                }

                if (ok)
                {
                    table.Rows.Add(row);
                }
            }

            return table;
        }

        private static bool Number(string value, out float result, string source, int line, string column, PlacementTable table)
        {
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return true;
            }

            table.Errors.Add(source + ":" + line + ": column '" + column + "' is not a number: '" + value + "'.");
            return false;
        }
    }
}
