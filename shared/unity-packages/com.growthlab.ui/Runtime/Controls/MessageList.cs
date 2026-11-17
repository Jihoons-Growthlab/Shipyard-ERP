using System;
using UnityEngine.UIElements;

namespace GrowthLab.UI
{
    /// <summary>
    /// Newest-first list of timestamped messages with a status colour, capped at <see cref="Capacity"/>.
    /// UXML: <c>&lt;gl:MessageList capacity="30" empty-text="이벤트 없음" /&gt;</c>.
    /// </summary>
    [UxmlElement]
    public partial class MessageList : VisualElement
    {
        public const string UssClassName = "gl-messages";
        public const string RowClassName = UssClassName + "__row";

        private readonly ScrollView _scroll;
        private readonly Label _empty;
        private int _capacity = 50;

        public MessageList()
        {
            AddToClassList(UssClassName);
            _empty = new Label("No messages") { name = "empty" };
            _empty.AddToClassList(UssClassName + "__empty");
            _scroll = new ScrollView(ScrollViewMode.Vertical) { name = "rows" };
            _scroll.AddToClassList(UssClassName + "__scroll");
            hierarchy.Add(_empty);
            hierarchy.Add(_scroll);
        }

        [UxmlAttribute]
        public int Capacity
        {
            get => _capacity;
            set
            {
                _capacity = Math.Max(1, value);
                Trim();
            }
        }

        [UxmlAttribute]
        public string EmptyText
        {
            get => _empty.text;
            set => _empty.text = value;
        }

        public int Count => _scroll.contentContainer.childCount;

        /// <summary>Adds a row at the top. Returns the row so callers can style it further.</summary>
        public VisualElement Add(StatusLevel level, string message, DateTime? time = null)
        {
            var row = new VisualElement();
            row.AddToClassList(RowClassName);
            row.AddToClassList(StatusBadge.ClassFor(level));
            var stamp = new Label((time ?? DateTime.Now).ToString("HH:mm:ss"));
            stamp.AddToClassList(RowClassName + "-time");
            var text = new Label(message);
            text.AddToClassList(RowClassName + "-text");
            row.Add(stamp);
            row.Add(text);
            _scroll.contentContainer.Insert(0, row);
            Trim();
            _empty.style.display = DisplayStyle.None;
            return row;
        }

        public new void Clear()
        {
            _scroll.contentContainer.Clear();
            _empty.style.display = DisplayStyle.Flex;
        }

        private void Trim()
        {
            var rows = _scroll.contentContainer;
            while (rows.childCount > _capacity)
            {
                rows.RemoveAt(rows.childCount - 1);
            }
        }
    }
}
