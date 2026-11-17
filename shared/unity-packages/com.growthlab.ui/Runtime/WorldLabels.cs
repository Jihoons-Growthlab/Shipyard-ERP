using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI
{
    /// <summary>
    /// Keeps UI Toolkit elements positioned over 3D objects (labels above equipment, markers).
    /// Put it next to a <see cref="UIDocument"/>, then call <see cref="Track"/>. Elements behind the camera
    /// are hidden. Elements ignore pointer input so they never block the dashboard.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class WorldLabels : MonoBehaviour
    {
        public const string LayerClassName = "gl-world-layer";
        public const string LabelClassName = "gl-world-label";

        [Tooltip("Camera used for projection. Empty = Camera.main.")]
        [SerializeField] private Camera _camera;

        private readonly List<Entry> _entries = new List<Entry>();
        private VisualElement _layer;

        public int Count => _entries.Count;

        /// <summary>Adds <paramref name="element"/> above <paramref name="target"/> at <paramref name="worldOffset"/>.</summary>
        public T Track<T>(Transform target, T element, Vector3 worldOffset) where T : VisualElement
        {
            EnsureLayer();
            element.AddToClassList(LabelClassName);
            element.pickingMode = PickingMode.Ignore;
            element.style.position = Position.Absolute;
            _layer.Add(element);
            _entries.Add(new Entry(target, element, worldOffset));
            return element;
        }

        public void Untrack(VisualElement element)
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Element == element)
                {
                    element.RemoveFromHierarchy();
                    _entries.RemoveAt(i);
                }
            }
        }

        private void EnsureLayer()
        {
            if (_layer != null && _layer.panel != null)
            {
                return;
            }

            var root = GetComponent<UIDocument>().rootVisualElement;
            _layer = new VisualElement { name = "world-labels", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList(LayerClassName);
            _layer.style.position = Position.Absolute;
            _layer.style.left = 0;
            _layer.style.top = 0;
            _layer.style.right = 0;
            _layer.style.bottom = 0;
            root.Insert(0, _layer); // behind the dashboard panels
        }

        private void LateUpdate()
        {
            if (_entries.Count == 0 || _layer?.panel == null)
            {
                return;
            }

            var cam = _camera != null ? _camera : Camera.main;
            if (cam == null)
            {
                return;
            }

            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (entry.Target == null)
                {
                    entry.Element.RemoveFromHierarchy();
                    _entries.RemoveAt(i);
                    continue;
                }

                var world = entry.Target.position + entry.Offset;
                var visible = cam.WorldToViewportPoint(world).z > 0f;
                entry.Element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible)
                {
                    continue;
                }

                var point = RuntimePanelUtils.CameraTransformWorldToPanel(_layer.panel, world, cam);
                var size = entry.Element.layout.size;
                entry.Element.style.left = point.x - (float.IsNaN(size.x) ? 0f : size.x / 2f);
                entry.Element.style.top = point.y - (float.IsNaN(size.y) ? 0f : size.y);
            }
        }

        private readonly struct Entry
        {
            public Entry(Transform target, VisualElement element, Vector3 offset)
            {
                Target = target;
                Element = element;
                Offset = offset;
            }

            public Transform Target { get; }

            public VisualElement Element { get; }

            public Vector3 Offset { get; }
        }
    }
}
