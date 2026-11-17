using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Dashboard shell around the erection panel (layout in Dashboard.uxml / Dashboard.uss): the left icon rail,
    /// drawers that slide out from it (erection, chat; one at a time), the depth-map toggle with its legend, and the
    /// exit button with a confirmation. Everything starts closed. Escape closes the dialog or the open drawer.
    /// While the pointer is over these controls <see cref="PointerOverUi"/> is true so the camera ignores it.
    /// Command line <c>-shipyard-panel</c> opens the erection drawer and <c>-shipyard-depth</c> turns the depth map on
    /// at start (screenshots).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DashboardShellView : MonoBehaviour
    {
        public const string PanelArgument = "-shipyard-panel";
        public const string DepthArgument = "-shipyard-depth";
        private const float RailWidth = 72f, DrawerGap = 14f, DrawerWidth = 360f;
        private const int SlideMilliseconds = 320;

        private readonly List<VisualElement> _interactive = new List<VisualElement>();
        private VisualElement _root, _erectionPanel, _chatPanel, _legend, _confirm;
        private Button _navErection, _navDepth, _navChat;
        private VisualElement _openDrawer;

        /// <summary>True while the mouse is over a dashboard control (camera input should be ignored).</summary>
        public static bool PointerOverUi { get; private set; }

        /// <summary>Depth map toggle state; raised when it changes.</summary>
        public bool DepthVisible { get; private set; }

        public event Action<bool> DepthVisibleChanged;

        private void Start()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null || _root.Q("rail") == null)
            {
                return;
            }

            _erectionPanel = _root.Q("erection-panel");
            _chatPanel = _root.Q("chat-panel");
            _legend = _root.Q("depth-legend");
            _confirm = _root.Q("exit-confirm");
            _navErection = _root.Q<Button>("nav-erection");
            _navDepth = _root.Q<Button>("nav-depth");
            _navChat = _root.Q<Button>("nav-chat");

            _interactive.Clear();
            foreach (var name in new[] { "rail", "erection-panel", "chat-panel", "depth-legend", "exit", "exit-confirm" })
            {
                var element = _root.Q(name);
                if (element != null) _interactive.Add(element);
            }

            Click("nav-erection", () => Toggle(_erectionPanel));
            Click("nav-chat", () => Toggle(_chatPanel));
            Click("erection-close", () => Open(null));
            Click("chat-close", () => Open(null));
            Click("nav-depth", () => SetDepth(!DepthVisible));
            Click("exit", () => SetConfirm(true));
            Click("exit-cancel", () => SetConfirm(false));
            Click("exit-confirm-button", DashboardExit.Quit);
#if UNITY_EDITOR
            var detail = _root.Q<Label>("exit-detail");
            if (detail != null) detail.text = "Play Mode를 종료합니다.";
#endif
            _root.RegisterCallback<GeometryChangedEvent>(_ => FitDrawers());

            if (Array.IndexOf(Environment.GetCommandLineArgs(), PanelArgument) >= 0)
            {
                Open(_erectionPanel, instant: true);
            }

            if (Array.IndexOf(Environment.GetCommandLineArgs(), DepthArgument) >= 0)
            {
                SetDepth(true);
            }
        }

        /// <summary>Fills the depth legend: status badge, detail text and colour bands (null bands: no scale).</summary>
        public void SetDepthLegend(string status, string detail, IReadOnlyList<(Color Color, string Label)> bands)
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            var statusLabel = root.Q<Label>("depth-status");
            if (statusLabel != null)
            {
                statusLabel.text = status;
                statusLabel.EnableInClassList("tw-status--muted", bands == null);
            }

            var detailLabel = root.Q<Label>("depth-detail");
            if (detailLabel != null) detailLabel.text = detail;
            var scale = root.Q("depth-scale");
            if (scale == null)
            {
                return;
            }

            scale.Clear();
            scale.style.display = bands == null ? DisplayStyle.None : DisplayStyle.Flex;
            if (bands == null)
            {
                return;
            }

            foreach (var (color, label) in bands)
            {
                var band = new VisualElement();
                band.AddToClassList("tw-scale-band");
                var swatch = new VisualElement();
                swatch.AddToClassList("tw-scale-swatch");
                swatch.style.backgroundColor = color;
                band.Add(swatch);
                var text = new Label(label);
                text.AddToClassList("tw-scale-label");
                band.Add(text);
                scale.Add(band);
            }

            var unit = new Label("m");
            unit.AddToClassList("tw-scale-unit");
            scale.Add(unit);
        }

        private void OnDisable() => PointerOverUi = false;

        private void Update()
        {
            PointerOverUi = IsPointerOverControls();
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CloseTopmost();
            }
        }

        private void Click(string name, Action action)
        {
            var button = _root.Q<Button>(name);
            if (button != null) button.clicked += action;
        }

        private void Toggle(VisualElement drawer) => Open(_openDrawer == drawer ? null : drawer);

        /// <summary>Opens one drawer (closing the other) or closes all with null.</summary>
        private void Open(VisualElement drawer, bool instant = false)
        {
            _openDrawer = drawer;
            foreach (var d in new[] { _erectionPanel, _chatPanel })
            {
                if (d != null) SetDrawer(d, d == drawer, instant);
            }

            _navErection?.EnableInClassList("tw-nav--active", drawer != null && drawer == _erectionPanel);
            _navChat?.EnableInClassList("tw-nav--active", drawer != null && drawer == _chatPanel);
        }

        private void SetDrawer(VisualElement drawer, bool open, bool instant)
        {
            drawer.EnableInClassList("tw-drawer--instant", instant);
            if (open)
            {
                drawer.style.visibility = Visibility.Visible;
                if (instant)
                {
                    drawer.AddToClassList("tw-drawer--open");
                }
                else
                {
                    // Next frame, so the closed state is laid out first and the slide transition runs.
                    drawer.schedule.Execute(() =>
                    {
                        if (_openDrawer == drawer) drawer.AddToClassList("tw-drawer--open");
                    });
                }

                return;
            }

            if (!drawer.ClassListContains("tw-drawer--open") && drawer.style.visibility != Visibility.Visible)
            {
                return;   // already closed
            }

            drawer.RemoveFromClassList("tw-drawer--open");
            drawer.Blur();
            if (instant)
            {
                drawer.style.visibility = Visibility.Hidden;
                return;
            }

            // Hide after the slide-out so a closed drawer cannot be clicked or focused.
            drawer.schedule.Execute(() =>
            {
                if (_openDrawer != drawer) drawer.style.visibility = Visibility.Hidden;
            }).ExecuteLater(SlideMilliseconds);
        }

        private void SetDepth(bool visible)
        {
            DepthVisible = visible;
            _navDepth?.EnableInClassList("tw-nav--active", visible);
            _legend?.EnableInClassList("tw-legend--show", visible);
            DepthVisibleChanged?.Invoke(visible);
        }

        private void SetConfirm(bool show)
        {
            _confirm?.EnableInClassList("tw-backdrop--show", show);
            if (show) _root.Q<Button>("exit-cancel")?.Focus();
        }

        private void CloseTopmost()
        {
            if (_confirm != null && _confirm.ClassListContains("tw-backdrop--show"))
            {
                SetConfirm(false);
            }
            else if (_openDrawer != null)
            {
                Open(null);
            }
        }

        private void FitDrawers()
        {
            var width = Mathf.Max(220f, Mathf.Min(DrawerWidth, _root.layout.width - RailWidth - DrawerGap * 2f));
            if (_erectionPanel != null) _erectionPanel.style.width = width;
            if (_chatPanel != null) _chatPanel.style.width = width;
        }

        private bool IsPointerOverControls()
        {
            var panel = _root?.panel;
            if (panel == null)
            {
                return false;
            }

            var mouse = Input.mousePosition;
            var position = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(mouse.x, Screen.height - mouse.y));
            for (var picked = panel.Pick(position); picked != null; picked = picked.parent)
            {
                if (_interactive.Contains(picked))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
