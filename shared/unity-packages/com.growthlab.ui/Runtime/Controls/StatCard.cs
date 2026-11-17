using UnityEngine.UIElements;

namespace GrowthLab.UI
{
    /// <summary>
    /// Card with a title row (title + <see cref="StatusBadge"/>) and a body. Children declared in UXML go
    /// into the body: <c>&lt;gl:StatCard title="pump-01"&gt;&lt;gl:Gauge .../&gt;&lt;/gl:StatCard&gt;</c>.
    /// </summary>
    [UxmlElement]
    public partial class StatCard : VisualElement
    {
        public const string UssClassName = "gl-card";

        private readonly Label _title;
        private readonly VisualElement _body;

        public StatCard()
        {
            AddToClassList(UssClassName);
            var header = new VisualElement { name = "header" };
            header.AddToClassList(UssClassName + "__header");
            _title = new Label { name = "title" };
            _title.AddToClassList(UssClassName + "__title");
            Badge = new StatusBadge { name = "badge" };
            header.Add(_title);
            header.Add(Badge);
            hierarchy.Add(header);

            _body = new VisualElement { name = "body" };
            _body.AddToClassList(UssClassName + "__body");
            hierarchy.Add(_body);
        }

        [UxmlAttribute]
        public string Title
        {
            get => _title.text;
            set => _title.text = value;
        }

        /// <summary>Shortcut for <c>Badge.Level</c>.</summary>
        [UxmlAttribute]
        public StatusLevel Level
        {
            get => Badge.Level;
            set => Badge.Level = value;
        }

        public StatusBadge Badge { get; }

        public override VisualElement contentContainer => _body;
    }
}
