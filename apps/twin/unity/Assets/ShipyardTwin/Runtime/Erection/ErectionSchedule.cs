using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShipyardTwin
{
    /// <summary>
    /// Erection timeline in days for one crane: blocks are lifted one at a time. Blocks sharing an erection day
    /// (actual, else forecast, else baseline) are spread evenly, in step and id order, over the time since the
    /// previous day's last landing, so the last of them lands on its day. A lift lasts <see cref="LiftDays"/> or
    /// its share of that window, whichever is shorter. Blocks without a day follow the last dated block, one lift
    /// apart. Day 0 is the start of the first lift.
    /// </summary>
    public sealed class ErectionSchedule
    {
        private readonly List<Entry> _entries;

        public ErectionSchedule(IEnumerable<BlockRecord> blocks, float liftDays)
        {
            if (liftDays <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(liftDays), "Lift must take more than 0 days.");
            }

            LiftDays = liftDays;
            var dated = new List<(BlockRecord Block, DateTime? Day)>();
            foreach (var block in blocks ?? Enumerable.Empty<BlockRecord>())
            {
                dated.Add((block, ParseDay(block.Day)));
            }

            var known = dated.Where(d => d.Day.HasValue).Select(d => d.Day.Value).ToList();
            var first = known.Count > 0 ? known.Min() : DateTime.Today;
            Start = first.AddDays(-liftDays);

            _entries = new List<Entry>();
            var previous = 0f;
            var groups = dated.Where(d => d.Day.HasValue).GroupBy(d => d.Day.Value).OrderBy(g => g.Key);
            foreach (var group in groups)
            {
                var day = (float)(group.Key - Start).TotalDays;
                var members = group.OrderBy(d => d.Block.step <= 0 ? int.MaxValue : d.Block.step)
                    .ThenBy(d => d.Block.block, StringComparer.Ordinal).ToList();
                var slot = (day - previous) / members.Count;
                for (var i = 0; i < members.Count; i++)
                {
                    _entries.Add(new Entry(members[i].Block, previous + slot * (i + 1), Math.Min(liftDays, slot)));
                }

                previous = day;
            }

            foreach (var (block, _) in dated.Where(d => !d.Day.HasValue)
                         .OrderBy(d => d.Block.step <= 0 ? int.MaxValue : d.Block.step).ThenBy(d => d.Block.block, StringComparer.Ordinal))
            {
                previous += liftDays;
                _entries.Add(new Entry(block, previous, liftDays));
            }

            Duration = _entries.Count > 0 ? _entries.Max(e => e.FinishDay) : 0f;
            TotalWeight = _entries.Sum(e => e.Block.weight_t);
        }

        public float LiftDays { get; }

        /// <summary>Calendar day at cursor 0 (the first lift begins).</summary>
        public DateTime Start { get; }

        /// <summary>Cursor value when every block is erected.</summary>
        public float Duration { get; }

        public float TotalWeight { get; }

        public float Cursor { get; private set; }

        public DateTime CurrentDate => Start.AddDays(Cursor);

        public IReadOnlyList<Entry> Entries => _entries;

        public int ErectedCount { get; private set; }

        public float ErectedWeight { get; private set; }

        /// <summary>Highest step among blocks that are lifting or erected (0 before the first lift).</summary>
        public int CurrentStep { get; private set; }

        public bool Finished => Cursor >= Duration;

        /// <summary>0 = not started, between 0 and 1 = being lifted, 1 = erected.</summary>
        public float Progress(Entry entry)
        {
            var begin = entry.FinishDay - entry.LiftDays;
            if (Cursor <= begin)
            {
                return 0f;
            }

            return Cursor >= entry.FinishDay ? 1f : (Cursor - begin) / entry.LiftDays;
        }

        /// <summary>Moves the cursor (clamped to 0..Duration) and returns blocks erected by this move, in order.</summary>
        public IReadOnlyList<Entry> Seek(float cursor)
        {
            var previous = Cursor;
            Cursor = Math.Max(0f, Math.Min(Duration, cursor));
            var newly = new List<Entry>();
            ErectedCount = 0;
            ErectedWeight = 0f;
            CurrentStep = 0;
            foreach (var entry in _entries)
            {
                var progress = Progress(entry);
                if (progress >= 1f)
                {
                    ErectedCount++;
                    ErectedWeight += entry.Block.weight_t;
                    if (entry.FinishDay > previous)
                    {
                        newly.Add(entry);
                    }
                }

                if (progress > 0f)
                {
                    CurrentStep = Math.Max(CurrentStep, entry.Block.step);
                }
            }

            return newly;
        }

        public IReadOnlyList<Entry> Advance(float days) => Seek(Cursor + days);

        /// <summary>Blocks currently being lifted.</summary>
        public IEnumerable<Entry> Lifting()
        {
            foreach (var entry in _entries)
            {
                var progress = Progress(entry);
                if (progress > 0f && progress < 1f)
                {
                    yield return entry;
                }
            }
        }

        private static DateTime? ParseDay(string value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : (DateTime?)null;

        public sealed class Entry
        {
            public Entry(BlockRecord block, float finishDay, float liftDays)
            {
                Block = block;
                FinishDay = finishDay;
                LiftDays = liftDays;
            }

            public BlockRecord Block { get; }

            /// <summary>Cursor value when the block is landed.</summary>
            public float FinishDay { get; }

            /// <summary>How long this lift takes (days).</summary>
            public float LiftDays { get; }
        }
    }

    /// <summary>Play / pause / speed state shared by the 3D view, crane and panel.</summary>
    public sealed class ErectionPlayback
    {
        public static readonly float[] Speeds = { 1f, 2f, 4f, 8f };

        public ErectionPlayback(ErectionSchedule schedule, float daysPerSecond, bool playing)
        {
            Schedule = schedule;
            DaysPerSecond = daysPerSecond;
            Playing = playing;
        }

        public ErectionSchedule Schedule { get; }

        public bool Playing { get; set; }

        /// <summary>Simulation days per real second at speed ×1.</summary>
        public float DaysPerSecond { get; }

        public float SpeedMultiplier { get; private set; } = 1f;

        public event Action<ErectionSchedule.Entry> BlockErected;

        public void Tick(float seconds)
        {
            if (!Playing)
            {
                return;
            }

            Publish(Schedule.Advance(seconds * DaysPerSecond * SpeedMultiplier));
            if (Schedule.Finished)
            {
                Playing = false;
            }
        }

        public void Seek(float cursor) => Publish(Schedule.Seek(cursor));

        public void Restart()
        {
            Schedule.Seek(0f);
            Playing = true;
        }

        public float NextSpeed()
        {
            var index = Array.IndexOf(Speeds, SpeedMultiplier);
            SpeedMultiplier = Speeds[(index + 1) % Speeds.Length];
            return SpeedMultiplier;
        }

        private void Publish(IReadOnlyList<ErectionSchedule.Entry> erected)
        {
            foreach (var entry in erected)
            {
                BlockErected?.Invoke(entry);
            }
        }
    }
}
