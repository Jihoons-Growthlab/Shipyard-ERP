using System;
using UnityEngine.UIElements;

namespace GrowthLab.UI
{
    public enum StatusLevel
    {
        Normal,
        Info,
        Warning,
        Alarm,
        Offline,
    }

    /// <summary>
    /// Small coloured pill showing a status. UXML: <c>&lt;gl:StatusBadge level="Warning" text="점검" /&gt;</c>.
    /// Without a text the level name is shown.
    /// </summary>
    [UxmlElement]
    public partial class StatusBadge : Label
    {
        public const string UssClassName = "gl-badge";

        private StatusLevel _level;
        private bool _hasCustomText;

        public StatusBadge() : this(StatusLevel.Normal)
        {
        }

        public StatusBadge(StatusLevel level, string label = null)
        {
            AddToClassList(UssClassName);
            Level = level;
            if (label != null)
            {
                text = label;
            }
        }

        [UxmlAttribute]
        public StatusLevel Level
        {
            get => _level;
            set
            {
                RemoveFromClassList(ClassFor(_level));
                _level = value;
                AddToClassList(ClassFor(_level));
                if (!_hasCustomText)
                {
                    base.text = _level.ToString();
                }
            }
        }

        /// <summary>Text shown instead of the level name; empty restores the level name.</summary>
        public override string text
        {
            get => base.text;
            set
            {
                _hasCustomText = !string.IsNullOrEmpty(value);
                base.text = _hasCustomText ? value : _level.ToString();
            }
        }

        public static string ClassFor(StatusLevel level) => UssClassName + "--" + level.ToString().ToLowerInvariant();

        /// <summary>Case-insensitive parse; unknown values map to <see cref="StatusLevel.Info"/>.</summary>
        public static StatusLevel Parse(string value) =>
            Enum.TryParse(value, true, out StatusLevel level) ? level : StatusLevel.Info;
    }
}
