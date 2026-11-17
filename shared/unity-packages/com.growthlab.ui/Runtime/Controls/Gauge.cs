using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace GrowthLab.UI
{
    /// <summary>
    /// Horizontal bar gauge with label, value text and warning/alarm thresholds.
    /// UXML: <c>&lt;gl:Gauge label="온도" unit="°C" min="0" max="120" warning-at="75" alarm-at="82" /&gt;</c>.
    /// Thresholds are off while they are NaN (the default). Set <see cref="Value"/> from code.
    /// </summary>
    [UxmlElement]
    public partial class Gauge : VisualElement
    {
        public const string UssClassName = "gl-gauge";
        public const string LabelClassName = UssClassName + "__label";
        public const string ValueClassName = UssClassName + "__value";
        public const string TrackClassName = UssClassName + "__track";
        public const string FillClassName = UssClassName + "__fill";
        public const string WarningClassName = UssClassName + "--warning";
        public const string AlarmClassName = UssClassName + "--alarm";

        private readonly Label _label;
        private readonly Label _valueText;
        private readonly VisualElement _fill;
        private float _min;
        private float _max = 100f;
        private float _value;
        private float _warningAt = float.NaN;
        private float _alarmAt = float.NaN;
        private int _decimals = 1;
        private string _unit = string.Empty;

        public Gauge()
        {
            AddToClassList(UssClassName);
            var header = new VisualElement { name = "header" };
            header.AddToClassList(UssClassName + "__header");
            _label = new Label { name = "label" };
            _label.AddToClassList(LabelClassName);
            _valueText = new Label { name = "value" };
            _valueText.AddToClassList(ValueClassName);
            header.Add(_label);
            header.Add(_valueText);

            var track = new VisualElement { name = "track" };
            track.AddToClassList(TrackClassName);
            _fill = new VisualElement { name = "fill" };
            _fill.AddToClassList(FillClassName);
            track.Add(_fill);

            Add(header);
            Add(track);
            Refresh();
        }

        [UxmlAttribute]
        public string Label
        {
            get => _label.text;
            set => _label.text = value;
        }

        [UxmlAttribute]
        public string Unit
        {
            get => _unit;
            set
            {
                _unit = value ?? string.Empty;
                Refresh();
            }
        }

        [UxmlAttribute]
        public float Min
        {
            get => _min;
            set
            {
                _min = value;
                Refresh();
            }
        }

        [UxmlAttribute]
        public float Max
        {
            get => _max;
            set
            {
                _max = value;
                Refresh();
            }
        }

        [UxmlAttribute]
        public float Value
        {
            get => _value;
            set
            {
                _value = value;
                Refresh();
            }
        }

        /// <summary>Value at or above which the gauge turns yellow. NaN disables.</summary>
        [UxmlAttribute]
        public float WarningAt
        {
            get => _warningAt;
            set
            {
                _warningAt = value;
                Refresh();
            }
        }

        /// <summary>Value at or above which the gauge turns red. NaN disables.</summary>
        [UxmlAttribute]
        public float AlarmAt
        {
            get => _alarmAt;
            set
            {
                _alarmAt = value;
                Refresh();
            }
        }

        [UxmlAttribute]
        public int Decimals
        {
            get => _decimals;
            set
            {
                _decimals = Mathf.Clamp(value, 0, 6);
                Refresh();
            }
        }

        /// <summary>Fill fraction between 0 and 1.</summary>
        public float Ratio => _max > _min ? Mathf.Clamp01((_value - _min) / (_max - _min)) : 0f;

        public StatusLevel Level =>
            !float.IsNaN(_alarmAt) && _value >= _alarmAt ? StatusLevel.Alarm
            : !float.IsNaN(_warningAt) && _value >= _warningAt ? StatusLevel.Warning
            : StatusLevel.Normal;

        public string ValueText => _valueText.text;

        private void Refresh()
        {
            _valueText.text = _value.ToString("F" + _decimals, CultureInfo.InvariantCulture) +
                              (string.IsNullOrEmpty(_unit) ? string.Empty : " " + _unit);
            _fill.style.width = Length.Percent(Ratio * 100f);
            var level = Level;
            EnableInClassList(WarningClassName, level == StatusLevel.Warning);
            EnableInClassList(AlarmClassName, level == StatusLevel.Alarm);
        }
    }
}
