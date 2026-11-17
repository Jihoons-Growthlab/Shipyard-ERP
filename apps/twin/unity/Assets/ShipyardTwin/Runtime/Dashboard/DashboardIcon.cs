using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Vector icon for the dashboard (no font glyphs needed). Drawn on a 24×24 grid with the element's text colour,
    /// so USS <c>color</c> (hover, active) tints it. Glyphs: mark, crane, depth, chat, power, close.
    /// </summary>
    [UxmlElement]
    public partial class DashboardIcon : VisualElement
    {
        private string _glyph = "mark";

        public DashboardIcon()
        {
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(_ => MarkDirtyRepaint());
        }

        [UxmlAttribute]
        public string Glyph
        {
            get => _glyph;
            set
            {
                _glyph = value;
                MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            var rect = contentRect;
            if (rect.width <= 0f || rect.height <= 0f)
            {
                return;
            }

            var s = Mathf.Min(rect.width, rect.height) / 24f;
            var o = new Vector2(rect.x + (rect.width - 24f * s) / 2f, rect.y + (rect.height - 24f * s) / 2f);
            Vector2 P(float x, float y) => o + new Vector2(x, y) * s;

            var p = context.painter2D;
            p.strokeColor = resolvedStyle.color;
            p.fillColor = resolvedStyle.color;
            p.lineWidth = 1.8f * s;
            p.lineCap = LineCap.Round;
            p.lineJoin = LineJoin.Round;

            switch (_glyph)
            {
                case "crane":
                    p.BeginPath();
                    p.MoveTo(P(6, 21)); p.LineTo(P(6, 4)); p.LineTo(P(21, 4));    // mast and jib
                    p.MoveTo(P(6, 8)); p.LineTo(P(10, 4));                         // brace
                    p.MoveTo(P(3, 21)); p.LineTo(P(10, 21));                       // base
                    p.MoveTo(P(17, 4)); p.LineTo(P(17, 11));                       // hoist rope
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(14, 11)); p.LineTo(P(20, 11)); p.LineTo(P(20, 16)); p.LineTo(P(14, 16)); p.ClosePath();   // block
                    p.Stroke();
                    break;
                case "depth":
                    p.BeginPath();
                    for (var i = 0; i < 3; i++)
                    {
                        var y = 7f + i * 5f;
                        p.MoveTo(P(3, y));
                        p.BezierCurveTo(P(6, y - 3), P(9, y + 3), P(12, y));
                        p.BezierCurveTo(P(15, y - 3), P(18, y + 3), P(21, y));
                    }

                    p.Stroke();
                    break;
                case "chat":
                    p.BeginPath();
                    p.MoveTo(P(6, 4)); p.LineTo(P(18, 4));
                    p.ArcTo(P(21, 4), P(21, 7), 3f * s);
                    p.LineTo(P(21, 13));
                    p.ArcTo(P(21, 16), P(18, 16), 3f * s);
                    p.LineTo(P(11, 16)); p.LineTo(P(6, 20)); p.LineTo(P(7, 16)); p.LineTo(P(6, 16));
                    p.ArcTo(P(3, 16), P(3, 13), 3f * s);
                    p.LineTo(P(3, 7));
                    p.ArcTo(P(3, 4), P(6, 4), 3f * s);
                    p.ClosePath();
                    p.Stroke();
                    for (var i = 0; i < 3; i++)
                    {
                        p.BeginPath();
                        p.Arc(P(8 + i * 4, 10), 1.1f * s, Angle.Degrees(0f), Angle.Degrees(360f));
                        p.Fill();
                    }

                    break;
                case "power":
                    p.BeginPath();
                    p.Arc(P(12, 13), 7.5f * s, Angle.Degrees(-50f), Angle.Degrees(230f));
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(12, 3)); p.LineTo(P(12, 12));
                    p.Stroke();
                    break;
                case "close":
                    p.BeginPath();
                    p.MoveTo(P(6, 6)); p.LineTo(P(18, 18));
                    p.MoveTo(P(18, 6)); p.LineTo(P(6, 18));
                    p.Stroke();
                    break;
                default:   // mark: hull plan in a diamond
                    p.BeginPath();
                    p.MoveTo(P(12, 2)); p.LineTo(P(22, 12)); p.LineTo(P(12, 22)); p.LineTo(P(2, 12)); p.ClosePath();
                    p.Stroke();
                    p.BeginPath();
                    p.MoveTo(P(12, 7)); p.LineTo(P(15, 11)); p.LineTo(P(15, 17)); p.LineTo(P(9, 17)); p.LineTo(P(9, 11)); p.ClosePath();
                    p.Fill();
                    break;
            }
        }
    }
}
