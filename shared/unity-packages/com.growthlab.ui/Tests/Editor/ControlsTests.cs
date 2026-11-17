using NUnit.Framework;
using UnityEngine.UIElements;

namespace GrowthLab.UI.Tests
{
    public sealed class ControlsTests
    {
        [Test]
        public void Gauge_ClampsRatio_FormatsValue_AndSwitchesLevel()
        {
            var gauge = new Gauge { Min = 40, Max = 100, Unit = "°C", Decimals = 1, WarningAt = 75, AlarmAt = 82 };

            gauge.Value = 70;
            Assert.AreEqual(0.5f, gauge.Ratio, 1e-4);
            Assert.AreEqual("70.0 °C", gauge.ValueText);
            Assert.AreEqual(StatusLevel.Normal, gauge.Level);

            gauge.Value = 76;
            Assert.AreEqual(StatusLevel.Warning, gauge.Level);
            Assert.IsTrue(gauge.ClassListContains(Gauge.WarningClassName));

            gauge.Value = 500;
            Assert.AreEqual(1f, gauge.Ratio);
            Assert.AreEqual(StatusLevel.Alarm, gauge.Level);
            Assert.IsFalse(gauge.ClassListContains(Gauge.WarningClassName));
            Assert.IsTrue(gauge.ClassListContains(Gauge.AlarmClassName));
        }

        [Test]
        public void Gauge_WithoutThresholds_StaysNormal()
        {
            var gauge = new Gauge { Value = 1e6f };
            Assert.AreEqual(StatusLevel.Normal, gauge.Level);
        }

        [Test]
        public void StatusBadge_ShowsLevelName_UntilTextIsSet_AndSwapsClass()
        {
            var badge = new StatusBadge(StatusLevel.Warning);
            Assert.AreEqual("Warning", badge.text);
            Assert.IsTrue(badge.ClassListContains("gl-badge--warning"));

            badge.Level = StatusLevel.Alarm;
            Assert.IsFalse(badge.ClassListContains("gl-badge--warning"));
            Assert.IsTrue(badge.ClassListContains("gl-badge--alarm"));
            Assert.AreEqual("Alarm", badge.text);

            badge.text = "경보";
            badge.Level = StatusLevel.Normal;
            Assert.AreEqual("경보", badge.text);

            Assert.AreEqual(StatusLevel.Alarm, StatusBadge.Parse("ALARM"));
            Assert.AreEqual(StatusLevel.Info, StatusBadge.Parse("unknown"));
        }

        [Test]
        public void StatCard_PutsChildrenInBody()
        {
            var card = new StatCard { Title = "pump-01", Level = StatusLevel.Warning };
            var gauge = new Gauge();
            card.Add(gauge);

            Assert.AreSame(card, gauge.parent, "Logical parent is the card.");
            Assert.AreEqual("body", gauge.hierarchy.parent.name, "Physical parent is the body.");
            Assert.AreEqual(StatusLevel.Warning, card.Badge.Level);
        }

        [Test]
        public void MessageList_AddsNewestFirst_AndKeepsCapacity()
        {
            var list = new MessageList { Capacity = 2 };
            list.Add(StatusLevel.Info, "a");
            list.Add(StatusLevel.Warning, "b");
            var newest = list.Add(StatusLevel.Alarm, "c");

            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("c", newest.Q<Label>(className: MessageList.RowClassName + "-text").text);
            Assert.AreSame(newest, newest.parent[0]);

            list.Clear();
            Assert.AreEqual(0, list.Count);
        }
    }
}
