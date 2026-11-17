using System.Collections.Generic;
using System.Linq;
using GrowthLab.Core;
using GrowthLab.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace ShipyardTwin
{
    /// <summary>
    /// Erection drawer (Dashboard.uxml): ERP data status, hull chips from the yard plan, yard and unplaced counts, and
    /// for the selected hull the simulation (date, step, progress gauges, play/speed/restart, block on the hook,
    /// erection log). Choosing a hull only prepares it; the play button starts it. A world label follows the lifted block.
    ///
    /// Updates are event driven: chips, counts and texts are rebuilt when the plan, the selection or the data status
    /// change; while playing the progress texts refresh at most <see cref="RefreshInterval"/> times a second, and
    /// nothing is rebuilt while paused.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ErectionPanelView : MonoBehaviour, ICoreConsumer
    {
        private const float RefreshInterval = 0.1f;

        [SerializeField] private float _labelHeight = 18f;

        private ShipView _ship;
        private YardDataSource _source;
        private ErectionPlayback _bound;
        private VisualElement _root, _hullList, _sim;
        private Label _data, _summary, _placeholder, _hull, _date, _step, _status, _liftDetail, _unplaced, _unplacedTitle;
        private StatCard _liftCard;
        private Gauge _blocks, _weight;
        private MessageList _events;
        private Button _play, _speed, _restart;
        private WorldLabels _labels;
        private StatusBadge _hookLabel;
        private Transform _labelled;
        private float _shownCursor = -1f, _nextRefresh;
        private bool _shownPlaying;
        private float _shownSpeed;
        private BlockRecord _shownLift;

        public void Construct(CoreContext context)
        {
            if (_source != null || !context.Services.TryGet(out _source))
            {
                return;
            }

            _ship = FindFirstObjectByType<ShipView>();
            _labels = GetComponent<WorldLabels>();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _data = _root.Q<Label>("data-status");
            _hullList = _root.Q("hull-list");
            _summary = _root.Q<Label>("yard-summary");
            _sim = _root.Q("sim");
            _placeholder = _root.Q<Label>("sim-placeholder");
            _hull = _root.Q<Label>("hull");
            _date = _root.Q<Label>("date");
            _step = _root.Q<Label>("step");
            _status = _root.Q<Label>("status");
            _blocks = _root.Q<Gauge>("blocks");
            _weight = _root.Q<Gauge>("weight");
            _liftCard = _root.Q<StatCard>("lift");
            _liftDetail = _root.Q<Label>("lift-detail");
            _unplaced = _root.Q<Label>("unplaced");
            _unplacedTitle = _root.Q<Label>("unplaced-title");
            _events = _root.Q<MessageList>("events");
            _play = _root.Q<Button>("play");
            _speed = _root.Q<Button>("speed");
            _restart = _root.Q<Button>("restart");

            if (_play != null) _play.clicked += () =>
            {
                var playback = _ship?.Playback;
                if (playback != null) playback.Playing = !playback.Playing && !playback.Schedule.Finished;
                RefreshProgress();
            };
            if (_speed != null) _speed.clicked += () => { _ship?.Playback?.NextSpeed(); RefreshProgress(); };
            if (_restart != null) _restart.clicked += () =>
            {
                _events?.Clear();
                _ship?.Playback?.Restart();
                RefreshProgress();
            };

            _source.Changed += RefreshData;
            if (_ship != null) _ship.Changed += Rebuild;
            RefreshData();
            Rebuild();
        }

        private void OnDestroy()
        {
            if (_source != null) _source.Changed -= RefreshData;
            if (_ship != null) _ship.Changed -= Rebuild;
            Bind(null);
        }

        private void RefreshData()
        {
            if (_data == null)
            {
                return;
            }

            _data.text = _source.Detail;
            _data.EnableInClassList("tw-data-status--erp", _source.Status == YardDataStatus.Erp);
            _data.EnableInClassList("tw-data-status--loading", _source.Status == YardDataStatus.Loading);
            _data.EnableInClassList("tw-data-status--failed", _source.Status == YardDataStatus.Failed);
        }

        /// <summary>Plan or selection changed: chips, counts, unplaced list, simulation header; rebinds the playback.</summary>
        private void Rebuild()
        {
            var model = _ship?.Model;
            var selected = _ship?.Selected;
            Bind(_ship?.Playback);
            if (_hullList != null && model != null)
            {
                _hullList.Clear();
                foreach (var hull in model.Hulls)
                {
                    var chip = new Button { name = "hull-" + hull.Id, tooltip = hull.Name };
                    chip.AddToClassList("tw-chip");
                    chip.EnableInClassList("tw-chip--active", hull == selected);
                    var title = new Label(hull.Id);
                    title.AddToClassList("tw-chip-title");
                    var sub = new Label(Short(hull.Info?.vessel_type) + " · 블록 " + hull.Blocks.Count);
                    sub.AddToClassList("tw-chip-sub");
                    chip.Add(title);
                    chip.Add(sub);
                    var id = hull.Id;
                    chip.clicked += () => _ship.Select(_ship.Selected?.Id == id ? null : id);
                    _hullList.Add(chip);
                }

                if (model.Hulls.Count == 0)
                {
                    _hullList.Add(new Label("표시할 호선이 없습니다. ERP 연결이나 스냅샷(export_yard.py)을 확인하세요.") { name = "no-hulls" });
                }
            }

            if (_sim != null) _sim.style.display = selected != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (_placeholder != null) _placeholder.style.display = selected != null ? DisplayStyle.None : DisplayStyle.Flex;
            if (selected != null && _hull != null)
            {
                var info = selected.Info;
                var site = model.Site;
                _hull.text = selected.Id + " · " + info.vessel_type + "\n" + info.length_m + " × " + info.beam_m + " × " + info.depth_m + " m · 블록 " +
                             selected.Blocks.Count + "개 · " + (selected.Dock?.name ?? selected.Placement.dock) +
                             (site != null && !string.IsNullOrEmpty(site.id) ? "\n" + site.name : string.Empty);
            }

            if (_blocks != null && _ship?.Schedule != null) _blocks.Max = _ship.Schedule.Entries.Count;
            if (_weight != null && _ship?.Schedule != null) _weight.Max = Mathf.Max(1f, Mathf.Round(_ship.Schedule.TotalWeight));
            if (_play != null) _play.SetEnabled(selected != null);
            _events?.Clear();
            RefreshCounts();
            RefreshProgress();
        }

        /// <summary>Yard counts (plan day, or the selected hull at the simulation date) and the unplaced list.</summary>
        private void RefreshCounts()
        {
            var model = _ship?.Model;
            if (model == null)
            {
                return;
            }

            var selected = _ship.Selected;
            if (_summary != null)
            {
                if (selected == null)
                {
                    var all = model.Hulls.SelectMany(h => h.Blocks);
                    var c = YardModel.Count(all, model.AsOf);
                    _summary.text = "계획일 " + model.AsOf.ToString("yyyy-MM-dd") + " 기준 야드 적치 " + c.Stocked + " · 생산 중 " + c.InProduction +
                                    " · 탑재 완료 " + c.Erected + " · 미배치 " + c.Unplaced + " (호선 " + model.Hulls.Count + "척, 블록 " + model.Blocks.Count + "개)";
                }
                else
                {
                    _summary.text = selected.Id + " 시뮬레이션 · 야드 적치 " + _ship.CountIn(selected, BlockState.Stocked) + " · 인양 " +
                                    _ship.CountIn(selected, BlockState.Lifting) + " · 탑재 " + _ship.CountIn(selected, BlockState.Erected) +
                                    " · 생산 중 " + _ship.CountIn(selected, BlockState.InProduction) + " · 미배치 " + _ship.CountIn(selected, BlockState.Unplaced) +
                                    " · 다른 호선은 계획일 상태";
                }
            }

            if (_unplaced != null)
            {
                // Blocks with no stocking plan or slot at all (미배치 in the ERP plan), per hull.
                var lines = model.Hulls.Select(h => (h.Id, Blocks: h.Blocks.Where(b => b.Stays.Count == 0 && !(b.ErectionDay <= model.AsOf)).Select(b => b.Id).ToList()))
                    .Where(x => x.Blocks.Count > 0)
                    .Select(x => x.Id + " " + x.Blocks.Count + "개: " + string.Join(", ", x.Blocks.Take(12)) + (x.Blocks.Count > 12 ? " …" : string.Empty))
                    .ToList();
                _unplaced.text = lines.Count > 0 ? string.Join("\n", lines) : "없음 (모든 블록에 적치 계획과 좌표가 있습니다)";
                if (_unplacedTitle != null) _unplacedTitle.text = "미배치 (적치 계획·좌표 없음)";
            }
        }

        private void Bind(ErectionPlayback playback)
        {
            if (_bound == playback)
            {
                return;
            }

            if (_bound != null) _bound.BlockErected -= OnErected;
            _bound = playback;
            if (_bound != null) _bound.BlockErected += OnErected;
            _shownCursor = -1f;
        }

        private void OnErected(ErectionSchedule.Entry entry) =>
            _events?.Add(StatusLevel.Normal, entry.Block.block + " 탑재 완료 · " + entry.Block.weight_t.ToString("F0") + "t");

        private void Update()
        {
            var playback = _ship?.Playback;
            if (playback == null)
            {
                UpdateHookLabel(null);
                return;
            }

            var changed = !Mathf.Approximately(playback.Schedule.Cursor, _shownCursor) || playback.Playing != _shownPlaying ||
                          !Mathf.Approximately(playback.SpeedMultiplier, _shownSpeed) || _ship.CurrentLiftBlock != _shownLift;
            if (changed && Time.unscaledTime >= _nextRefresh)
            {
                RefreshProgress();
            }
        }

        /// <summary>Simulation texts and gauges; only called when something they show changed.</summary>
        private void RefreshProgress()
        {
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            var playback = _ship?.Playback;
            if (playback == null)
            {
                if (_play != null) _play.text = "재생";
                return;
            }

            var schedule = playback.Schedule;
            _shownCursor = schedule.Cursor;
            _shownPlaying = playback.Playing;
            _shownSpeed = playback.SpeedMultiplier;
            if (_date != null) _date.text = schedule.CurrentDate.ToString("yyyy-MM-dd") + (schedule.Finished ? "  · 탑재 완료" : string.Empty);
            if (_step != null) _step.text = "탑재 단계 " + schedule.CurrentStep + " / 블록 " + schedule.ErectedCount + "개 탑재";
            if (_blocks != null) _blocks.Value = schedule.ErectedCount;
            if (_weight != null) _weight.Value = Mathf.Round(schedule.ErectedWeight);
            if (_play != null) _play.text = playback.Playing ? "일시정지" : schedule.Finished ? "완료" : "재생";
            if (_speed != null) _speed.text = "속도 ×" + playback.SpeedMultiplier.ToString("0");
            if (_status != null)
            {
                var state = schedule.Finished ? "완료" : playback.Playing ? "재생 중" : schedule.Cursor > 0f ? "일시정지" : "대기";
                _status.text = state;
                _status.EnableInClassList("tw-status--playing", playback.Playing);
                _status.EnableInClassList("tw-status--paused", !playback.Playing && !schedule.Finished && schedule.Cursor > 0f);
            }

            var block = _ship.CurrentLiftBlock;
            _shownLift = block;
            if (_liftCard != null)
            {
                _liftCard.Title = block != null ? block.block : "대기";
                _liftCard.Level = block != null ? StatusLevel.Warning : StatusLevel.Offline;
            }

            if (_liftDetail != null)
            {
                _liftDetail.text = block != null
                    ? block.zone + "\n" + block.weight_t.ToString("F1") + "t · " + block.Size.x.ToString("F1") + " × " + block.Size.y.ToString("F1") +
                      " × " + block.Size.z.ToString("F1") + " m\n선행 " + (block.predecessors.Length > 0 ? string.Join(", ", block.predecessors) : "없음") +
                      (_ship.CurrentLiftHooked ? string.Empty : "\n트랜스포터로 PE장 이동 중")
                    : "인양 중인 블록 없음";
            }

            RefreshCounts();
            UpdateHookLabel(block);
        }

        private void UpdateHookLabel(BlockRecord block)
        {
            if (_labels == null)
            {
                return;
            }

            var target = block != null ? _ship.CurrentLift : null;
            if (target == _labelled)
            {
                return;
            }

            if (_hookLabel != null)
            {
                _labels.Untrack(_hookLabel);
                _hookLabel = null;
            }

            _labelled = target;
            if (target != null)
            {
                var id = block.block.Substring(block.block.LastIndexOf('-') + 1);
                _hookLabel = _labels.Track(target, new StatusBadge(StatusLevel.Warning, id + " 인양"), Vector3.up * _labelHeight);
            }
        }

        private static string Short(string vesselType)
        {
            if (string.IsNullOrEmpty(vesselType)) return "-";
            var cut = vesselType.IndexOf('(');
            return (cut > 0 ? vesselType.Substring(0, cut) : vesselType).Trim();
        }
    }
}
