using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI
{
    /// <summary>
    /// The GrowthLab style sheet (controls + layout classes such as <c>gl-screen</c>, <c>gl-panel</c>,
    /// <c>gl-row</c>). Panels created by the Authoring package get it through their theme automatically;
    /// call <see cref="Apply"/> only for panels you set up yourself.
    /// </summary>
    public static class GrowthLabTheme
    {
        public const string StyleSheetResource = "GrowthLabUI";

        private static StyleSheet _sheet;

        public static StyleSheet StyleSheet => _sheet != null ? _sheet : _sheet = Resources.Load<StyleSheet>(StyleSheetResource);

        public static void Apply(VisualElement root)
        {
            var sheet = StyleSheet;
            if (sheet != null && !root.styleSheets.Contains(sheet))
            {
                root.styleSheets.Add(sheet);
            }
        }
    }
}
