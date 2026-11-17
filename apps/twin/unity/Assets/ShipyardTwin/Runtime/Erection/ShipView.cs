using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Every block of every hull in the yard plan, one GameObject each, kept for the whole session and found by block
    /// ID: in its yard slot, on the hook, or on the hull. Without a selected hull each block shows its state on the plan
    /// day (<see cref="YardModel.AsOf"/>). Selecting a hull resets its erection simulation to day 0; playback then moves
    /// only that hull's blocks: slot → (transporter) → crane → hull, in ERP erection order. Other hulls stay as planned.
    ///
    /// Cost: objects are created over several frames within <see cref="_frameBudgetMs"/>; OBJ text is parsed on worker
    /// threads and turned into meshes on the main thread within the same budget; a block gets a mesh only when it is
    /// shown or its hull is selected, and blocks hidden again after a switch release theirs. Per frame only the blocks
    /// whose state changes (sorted cursor events) and those actually moving are touched; nothing runs while paused.
    /// Each OBJ material (paint) is one submesh on shared palette materials; a lifted block is tinted, not repainted.
    /// </summary>
    public sealed class ShipView : MonoBehaviour, ICoreConsumer
    {
        public const string HullArgument = "-shipyard-hull";

        [SerializeField] private Color _liftTint = new Color(1f, 0.55f, 0.1f);
        [SerializeField, Range(0f, 1f)] private float _liftTintAmount = 0.4f;

        [Tooltip("Main-thread time per frame for creating blocks and meshes (ms).")]
        [SerializeField] private float _frameBudgetMs = 4f;

        [Tooltip("OBJ files parsed in parallel on worker threads.")]
        [SerializeField] private int _parallelParses = 6;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        private readonly Dictionary<string, Actor> _actors = new Dictionary<string, Actor>(StringComparer.Ordinal);
        private readonly LinkedList<Actor> _loadQueue = new LinkedList<Actor>();
        private readonly List<(Actor Actor, Task<ObjMeshParser.Result> Parse)> _parsing = new List<(Actor, Task<ObjMeshParser.Result>)>();
        private readonly HashSet<string> _markedHulls = new HashSet<string>();
        private readonly List<(float At, Actor Actor)> _events = new List<(float, Actor)>();
        private readonly HashSet<Actor> _moving = new HashSet<Actor>();
        private readonly Stopwatch _clock = new Stopwatch();
        private List<Actor> _selectedActors = new List<Actor>();
        private Dictionary<string, string> _facilityNames = new Dictionary<string, string>();

        private YardDataSource _source;
        private YardModel _model;
        private ShipPalette _palette;
        private ShipyardTwinSettings _settings;
        private MaterialPropertyBlock _properties;
        private Material _textMaterial;
        private BlockMotion.Reach _reach;
        private int _next;
        private float _cursor = -1f;
        private bool _applying, _reapply;
        private string _pendingHull;
        private float? _pendingSeek;

        public YardModel Model => _model;
        public YardDataSource Source => _source;
        public HullPlan Selected { get; private set; }
        public ErectionPlayback Playback { get; private set; }
        public ErectionSchedule Schedule => Playback?.Schedule;
        public string ActiveDock => Selected?.Placement?.dock;

        /// <summary>True once the plan is placed and every block shown on the plan day has its mesh.</summary>
        public bool Ready { get; private set; }

        /// <summary>The block being lifted (earliest if several), or null.</summary>
        public Transform CurrentLift { get; private set; }
        public BlockRecord CurrentLiftBlock { get; private set; }
        public Bounds CurrentLiftBounds { get; private set; }

        /// <summary>False while the lifted block is still driven to the crane on the ground.</summary>
        public bool CurrentLiftHooked { get; private set; }

        public int ActorCount => _actors.Count;
        public int LoadedMeshes => _actors.Values.Count(a => a.Mesh != null);
        public int ShownCount => _actors.Values.Count(a => a.Shown);

        /// <summary>Meshes still being read or parsed.</summary>
        public bool Loading => _loadQueue.Count > 0 || _parsing.Count > 0 || _applying;

        /// <summary>Selection, playback object or plan changed (not raised per frame).</summary>
        public event Action Changed;

        public void Construct(CoreContext context)
        {
            if (_source != null || !context.Services.TryGet(out _source) || !context.Services.TryGet(out _palette))
            {
                return;
            }

            _settings = context.Settings.Get<ShipyardTwinSettings>();
            _properties = new MaterialPropertyBlock();
            _pendingHull = Argument(HullArgument);
            var seek = Argument(ShipyardTwinBootstrap.SeekArgument);
            if (float.TryParse(seek, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var days))
            {
                _pendingSeek = days;
            }

            _source.Changed += OnSourceChanged;
            PerfProbe.StartPlayback = () =>
            {
                if (Selected == null && _model.Hulls.Count > 0) Select(_model.Hulls[0].Id);
                if (Playback != null) Playback.Playing = true;
            };
            PerfProbe.StopPlayback = () => { if (Playback != null) Playback.Playing = false; };
            PerfProbe.Busy = () => Loading;
            PerfProbe.SwitchHull = i => { if (_model.Hulls.Count > 0) Select(_model.Hulls[i % _model.Hulls.Count].Id); };
            PerfProbe.Extra = () => new Dictionary<string, double>
            {
                { "blocks", _actors.Count }, { "loaded_meshes", LoadedMeshes }, { "shown_blocks", ShownCount }, { "hulls", _model?.Hulls.Count ?? 0 },
            };
            ApplyModel(_source.Model);
        }

        private void OnDestroy()
        {
            if (_source != null)
            {
                _source.Changed -= OnSourceChanged;
            }

            foreach (var actor in _actors.Values)
            {
                ReleaseMesh(actor);
            }
        }

        private void OnSourceChanged()
        {
            if (_source.Model != _model)
            {
                ApplyModel(_source.Model);
            }
        }

        // ------------------------------------------------------------------ plan → actors (diff by block ID)

        private void ApplyModel(YardModel model)
        {
            if (model == null)
            {
                return;
            }

            if (_applying)
            {
                _reapply = true;   // the running pass picks up the newest model when it finishes
                return;
            }

            StartCoroutine(ApplyModelSliced(model));
        }

        private System.Collections.IEnumerator ApplyModelSliced(YardModel model)
        {
            _applying = true;
            var keepSelection = Selected?.Id ?? _pendingHull;
            if (Selected != null)
            {
                Select(null);
            }

            _model = model;
            _facilityNames = model.Facilities.Where(f => !string.IsNullOrEmpty(f.location)).GroupBy(f => f.location)
                .ToDictionary(g => g.Key, g => HoverTarget.FacilityTitle(g.First()));
            var keep = new HashSet<string>(model.Blocks.Keys, StringComparer.Ordinal);
            foreach (var gone in _actors.Keys.Where(id => !keep.Contains(id)).ToList())
            {
                Remove(_actors[gone]);
            }

            _clock.Restart();
            foreach (var hull in model.Hulls)
            {
                foreach (var plan in hull.Blocks)
                {
                    if (!_actors.TryGetValue(plan.Id, out var actor))
                    {
                        actor = Create(plan, hull);
                        _actors[plan.Id] = actor;
                    }
                    else
                    {
                        if (actor.Plan.Record.model_asset != plan.Record.model_asset)
                        {
                            ReleaseMesh(actor);
                        }

                        actor.Plan = plan;
                        actor.Hull = hull;
                        actor.Target.Detail = ShipView.BlockDetail(plan.Record, hull.Info);
                    }

                    actor.Landed = BlockMotion.Landed(hull.Placement, plan.Record);
                    ApplyPlanDay(actor);
                    if (_clock.Elapsed.TotalMilliseconds > _frameBudgetMs)
                    {
                        yield return null;
                        _clock.Restart();
                    }
                }

                if (_markedHulls.Add(hull.Id))
                {
                    AddMarkings(hull);
                }
            }

            _applying = false;
            Changed?.Invoke();
            if (_reapply)
            {
                _reapply = false;
                ApplyModel(_source.Model);
                yield break;
            }

            if (keepSelection != null && model.Hull(keepSelection) != null)
            {
                _pendingHull = null;
                Select(keepSelection);
                if (_pendingSeek.HasValue)
                {
                    Playback.Seek(_pendingSeek.Value);
                    _pendingSeek = null;
                }
            }
        }

        private Actor Create(BlockPlan plan, HullPlan hull)
        {
            var go = new GameObject(plan.Id);
            go.transform.SetParent(transform, false);
            var actor = new Actor
            {
                Plan = plan, Hull = hull, Go = go, Transform = go.transform, Filter = go.AddComponent<MeshFilter>(),
                Renderer = go.AddComponent<MeshRenderer>(), Collider = go.AddComponent<BoxCollider>(),
            };
            actor.Renderer.enabled = false;
            actor.Collider.enabled = false;
            // Hover picks a simple box (the block's bounding box): no mesh collider cooking per block.
            var bounds = ShipCoordinates.LocalBounds(plan.Record);
            actor.Collider.center = bounds.center;
            actor.Collider.size = bounds.size;
            actor.Target = go.AddComponent<HoverTarget>();
            actor.Target.Kind = "블록";
            actor.Target.Title = plan.Id;
            actor.Target.Detail = BlockDetail(plan.Record, hull.Info);
            return actor;
        }

        private void Remove(Actor actor)
        {
            _actors.Remove(actor.Plan.Id);
            _moving.Remove(actor);
            ReleaseMesh(actor);
            Destroy(actor.Go);
        }

        // ------------------------------------------------------------------ meshes (lazy, off-thread parse)

        private void RequestMesh(Actor actor, bool urgent)
        {
            if (actor.Mesh != null || actor.Queued)
            {
                return;
            }

            actor.Queued = true;
            if (urgent) _loadQueue.AddFirst(actor);
            else _loadQueue.AddLast(actor);
        }

        private void ReleaseMesh(Actor actor)
        {
            actor.Queued = false;
            if (actor.Mesh != null)
            {
                actor.Filter.sharedMesh = null;
                Destroy(actor.Mesh);
                actor.Mesh = null;
                actor.Renderer.enabled = false;
            }
        }

        private void PumpMeshes()
        {
            if (_loadQueue.Count == 0 && _parsing.Count == 0)
            {
                if (!Ready && !_applying && _model != null)
                {
                    Ready = true;
                    PerfProbe.MarkReady();
                }

                return;
            }

            _clock.Restart();
            var folder = _settings.ModelFolder;
            while (_loadQueue.Count > 0 && _parsing.Count < _parallelParses && _clock.Elapsed.TotalMilliseconds < _frameBudgetMs)
            {
                var actor = _loadQueue.First.Value;
                _loadQueue.RemoveFirst();
                if (!actor.Queued || actor.Go == null)
                {
                    continue;
                }

                var asset = actor.Plan.Record.model_asset;
                var text = string.IsNullOrEmpty(asset) ? null : Resources.Load<TextAsset>(folder + asset);
                if (text == null)
                {
                    actor.Queued = false;
                    Assign(actor, ObjMeshParser.Box(ShipCoordinates.LocalBounds(actor.Plan.Record), actor.Plan.Id), new List<string> { ShipPalette.Fallback });
                    continue;
                }

                var source = text.text;
                Resources.UnloadAsset(text);
                _parsing.Add((actor, Task.Run(() => ObjMeshParser.Parse(source))));
            }

            for (var i = 0; i < _parsing.Count && _clock.Elapsed.TotalMilliseconds < _frameBudgetMs; i++)
            {
                var (actor, parse) = _parsing[i];
                if (!parse.IsCompleted)
                {
                    continue;
                }

                _parsing.RemoveAt(i--);
                if (!actor.Queued || actor.Go == null)
                {
                    continue;   // released or removed while parsing
                }

                actor.Queued = false;
                if (parse.IsFaulted)
                {
                    Assign(actor, ObjMeshParser.Box(ShipCoordinates.LocalBounds(actor.Plan.Record), actor.Plan.Id), new List<string> { ShipPalette.Fallback });
                    continue;
                }

                Assign(actor, ObjMeshParser.ToMesh(parse.Result, actor.Plan.Id), parse.Result.Materials);
            }

        }

        private void Assign(Actor actor, Mesh mesh, List<string> paints)
        {
            actor.Mesh = mesh;
            actor.Filter.sharedMesh = mesh;
            actor.Renderer.sharedMaterials = paints.Select(_palette.Get).ToArray();
            actor.Colors = paints.Select(p => ShipPalette.Lookup(p).Color).ToArray();
            WriteColors(actor);
            actor.Renderer.enabled = actor.Shown;
        }

        private void Update()
        {
            PumpMeshes();
            if (Playback == null)
            {
                return;
            }

            if (Playback.Playing)
            {
                Playback.Tick(Time.deltaTime);
            }

            var cursor = Schedule.Cursor;
            if (Mathf.Approximately(cursor, _cursor) && _moving.Count == 0)
            {
                return;
            }

            if (cursor < _cursor)
            {
                ApplySimulation();
            }
            else
            {
                while (_next < _events.Count && _events[_next].At < cursor)
                {
                    ApplySimulation(_events[_next].Actor);
                    _next++;
                }

                foreach (var actor in _moving.ToList())
                {
                    ApplySimulation(actor);
                }
            }

            _cursor = cursor;
            UpdateCurrentLift();
        }

        // ------------------------------------------------------------------ selection and simulation

        /// <summary>Selects a hull (null clears): stops the previous playback and resets this hull to simulation day 0.</summary>
        public void Select(string hullId)
        {
            UnityEngine.Debug.Log("[ShipyardTwin] Hull selected: " + (hullId ?? "(none)"));
            if (Playback != null)
            {
                Playback.Playing = false;
            }

            var previous = _selectedActors;
            _selectedActors = new List<Actor>();
            _moving.Clear();
            _events.Clear();
            Selected = hullId != null ? _model?.Hull(hullId) : null;
            foreach (var actor in previous)
            {
                actor.Entry = null;
                ApplyPlanDay(actor);
                if (!actor.Shown)
                {
                    ReleaseMesh(actor);   // back in production: no mesh kept for it
                }
            }

            CurrentLift = null;
            CurrentLiftBlock = null;
            if (Selected == null)
            {
                Playback = null;
                _cursor = -1f;
                Changed?.Invoke();
                return;
            }

            var schedule = new ErectionSchedule(Selected.Blocks.Select(b => b.Record), _settings.LiftDays);
            Playback = new ErectionPlayback(schedule, _settings.DaysPerSecond, false);
            _reach = BlockMotion.CraneReach(Selected.Dock);
            foreach (var entry in schedule.Entries)
            {
                if (!_actors.TryGetValue(entry.Block.block, out var actor))
                {
                    continue;
                }

                actor.Entry = entry;
                _selectedActors.Add(actor);
                RequestMesh(actor, true);
                var start = schedule.Start;
                float At(DateTime day) => (float)(day - start).TotalDays;
                _events.Add((entry.FinishDay - entry.LiftDays, actor));
                _events.Add((entry.FinishDay, actor));
                var stays = actor.Plan.Stays;
                for (var i = 0; i < stays.Count; i++)
                {
                    _events.Add((At(stays[i].From), actor));
                    if (stays[i].Until.HasValue) _events.Add((At(stays[i].Until.Value.AddDays(1)), actor));
                    if (i > 0) _events.Add((At(stays[i].From.AddDays(-1)), actor));   // transporter move to the next slot
                }
            }

            _events.Sort((a, b) => a.At.CompareTo(b.At));
            schedule.Seek(0f);
            _cursor = -1f;
            ApplySimulation();
            _cursor = schedule.Cursor;
            Changed?.Invoke();
        }

        /// <summary>
        /// Recomputes every block of the selected hull at the current cursor (selection, seek back, restart). Events are
        /// consumed only once the cursor has passed them: a block's state at exactly its event time is still the old one
        /// (e.g. progress 0 at the lift start), so it must be revisited when the cursor moves on.
        /// </summary>
        private void ApplySimulation()
        {
            _moving.Clear();
            foreach (var actor in _selectedActors)
            {
                ApplySimulation(actor);
            }

            var cursor = Schedule.Cursor;
            _next = 0;
            while (_next < _events.Count && _events[_next].At < cursor)
            {
                _next++;
            }
        }

        private void ApplySimulation(Actor actor)
        {
            if (actor.Entry == null)
            {
                return;
            }

            var schedule = Schedule;
            var progress = schedule.Progress(actor.Entry);
            var moving = false;
            if (progress >= 1f)
            {
                Place(actor, actor.Landed, BlockState.Erected, null);
            }
            else if (progress > 0f)
            {
                var liftDay = schedule.Start.AddDays(actor.Entry.FinishDay - actor.Entry.LiftDays).Date;
                var stay = actor.Plan.LiftSource(liftDay);
                var (pose, hooked) = BlockMotion.Lift(stay != null ? BlockMotion.InYard(actor.Plan.Record, stay) : (Pose?)null,
                    actor.Landed, progress, _reach);
                actor.Hooked = hooked;
                Place(actor, pose, BlockState.Lifting, stay);
                moving = true;
            }
            else
            {
                var date = schedule.CurrentDate;
                var stay = actor.Plan.StayAt(date.Date);
                if (stay == null)
                {
                    Hide(actor, actor.Plan.Stays.Count > 0 && date.Date < actor.Plan.Stays[0].From ? BlockState.InProduction : BlockState.Unplaced);
                }
                else
                {
                    var pose = BlockMotion.InYard(actor.Plan.Record, stay);
                    var index = actor.Plan.Stays.IndexOf(stay);
                    if (index + 1 < actor.Plan.Stays.Count)
                    {
                        // The day before the next stay a transporter drives it over.
                        var next = actor.Plan.Stays[index + 1];
                        var t = (float)((date - next.From.AddDays(-1)).TotalDays);
                        if (t > 0f && t < 1f)
                        {
                            var target = BlockMotion.InYard(actor.Plan.Record, next);
                            var s = Mathf.SmoothStep(0f, 1f, t);
                            pose = new Pose(Vector3.Lerp(pose.position, target.position, s), Quaternion.Slerp(pose.rotation, target.rotation, s));
                            moving = true;
                        }
                    }

                    Place(actor, pose, BlockState.Stocked, stay);
                }
            }

            if (moving) _moving.Add(actor);
            else _moving.Remove(actor);
        }

        /// <summary>Plan-day state (no hull selected, or other hulls): erected, in its slot, or hidden.</summary>
        private void ApplyPlanDay(Actor actor)
        {
            if (actor.Entry != null)
            {
                ApplySimulation(actor);
                return;
            }

            var day = _model.AsOf;
            var state = YardModel.StateAt(actor.Plan, day);
            switch (state)
            {
                case BlockState.Erected:
                    Place(actor, actor.Landed, state, null);
                    break;
                case BlockState.Stocked:
                    var stay = actor.Plan.StayAt(day);
                    Place(actor, BlockMotion.InYard(actor.Plan.Record, stay), state, stay);
                    break;
                default:
                    Hide(actor, state);
                    break;
            }
        }

        private void Place(Actor actor, Pose pose, BlockState state, StockStay stay)
        {
            actor.Transform.SetPositionAndRotation(pose.position, pose.rotation);
            SetState(actor, state, stay);
            SetShown(actor, true);
            Tint(actor, state == BlockState.Lifting);
        }

        private void Hide(Actor actor, BlockState state)
        {
            SetState(actor, state, null);
            SetShown(actor, false);
        }

        private void SetState(Actor actor, BlockState state, StockStay stay)
        {
            if (actor.State == state && actor.Stay == stay)
            {
                return;
            }

            actor.State = state;
            actor.Stay = stay;
            var where = state == BlockState.Stocked && stay != null
                ? "적치 " + (_facilityNames.TryGetValue(stay.Location, out var name) ? name : stay.Location)
                : state == BlockState.Lifting ? "인양 중" : state == BlockState.Erected ? "탑재 완료" : null;
            actor.Target.Detail = BlockDetail(actor.Plan.Record, actor.Hull.Info) + (where != null ? " · " + where : string.Empty);
        }

        private void SetShown(Actor actor, bool shown)
        {
            if (shown)
            {
                RequestMesh(actor, actor.Entry != null);
            }

            if (actor.Shown == shown)
            {
                return;
            }

            actor.Shown = shown;
            actor.Renderer.enabled = shown && actor.Mesh != null;
            actor.Collider.enabled = shown;
            foreach (var marker in actor.Markers)
            {
                marker.SetActive(shown);
            }
        }

        private void Tint(Actor actor, bool lifting)
        {
            if (actor.Tinted != lifting)
            {
                actor.Tinted = lifting;
                WriteColors(actor);
            }
        }

        private void WriteColors(Actor actor)
        {
            if (actor.Mesh == null)
            {
                return;   // written when the mesh arrives
            }

            if (!actor.Tinted)
            {
                // Untinted blocks use the palette materials as they are: no property block, so the SRP Batcher keeps
                // batching them (a property block on every block forced one draw per submesh per shadow cascade).
                actor.Renderer.SetPropertyBlock(null);
                for (var i = 0; i < actor.Colors.Length; i++)
                {
                    actor.Renderer.SetPropertyBlock(null, i);
                }

                return;
            }

            for (var i = 0; i < actor.Colors.Length; i++)
            {
                actor.Renderer.GetPropertyBlock(_properties, i);
                var color = actor.Tinted ? Color.Lerp(actor.Colors[i], _liftTint, _liftTintAmount) : actor.Colors[i];
                _properties.SetColor(ColorId, color);
                _properties.SetColor(BaseColorId, color);
                actor.Renderer.SetPropertyBlock(_properties, i);
            }
        }

        private void UpdateCurrentLift()
        {
            Actor lift = null;
            foreach (var actor in _moving)
            {
                if (actor.State == BlockState.Lifting && (lift == null || actor.Entry.FinishDay < lift.Entry.FinishDay))
                {
                    lift = actor;
                }
            }

            CurrentLift = lift?.Transform;
            CurrentLiftBlock = lift?.Plan.Record;
            CurrentLiftHooked = lift != null && lift.Hooked;
            if (lift != null)
            {
                CurrentLiftBounds = lift.Mesh != null
                    ? lift.Renderer.bounds
                    : new Bounds(lift.Transform.position, ShipCoordinates.LocalBounds(lift.Plan.Record).size);
            }
        }

        // ------------------------------------------------------------------ queries for the panel

        public IEnumerable<BlockPlan> BlocksIn(HullPlan hull, BlockState state) =>
            hull == null ? Enumerable.Empty<BlockPlan>() : hull.Blocks.Where(b => _actors.TryGetValue(b.Id, out var a) && a.State == state);

        public int CountIn(HullPlan hull, BlockState state) => BlocksIn(hull, state).Count();

        /// <summary>Hover text of a block: hull number, zone and weight (the title is the block ID).</summary>
        public static string BlockDetail(BlockRecord block, HullInfo hull) =>
            "호선 " + (hull?.hull ?? block.hull ?? "-") + (string.IsNullOrEmpty(block.zone) ? string.Empty : " · " + block.zone) +
            " · " + block.weight_t.ToString("0") + " t";

        /// <summary>Ship hover target (keel blocks and other hull fittings): vessel name, else the hull number.</summary>
        public static HoverTarget AttachShipTarget(GameObject root, HullInfo hull)
        {
            var name = string.IsNullOrWhiteSpace(hull.vessel_name) ? hull.hull : hull.vessel_name;
            return HoverTarget.Attach(root, "선박", name, "호선 " + hull.hull + (string.IsNullOrEmpty(hull.vessel_type) ? string.Empty : " · " + hull.vessel_type));
        }

        // ------------------------------------------------------------------ hull markings

        /// <summary>
        /// Painted hull markings as world-space text on the blocks that carry them (they follow the block, shown with
        /// it): ship's name on both bow sides, name and port of registry on the transom, draft marks every 2 m.
        /// </summary>
        private void AddMarkings(HullPlan plan)
        {
            var hull = plan.Info;
            var actors = plan.Blocks.Select(b => _actors[b.Id]).ToList();
            Actor Find(Func<BlockRecord, bool> predicate) => actors.FirstOrDefault(a => predicate(a.Plan.Record));
            var name = (hull.vessel_name ?? hull.hull ?? string.Empty).Split('(')[0].Trim().ToUpperInvariant();
            var halfBeam = hull.beam_m / 2f;
            foreach (var side in new[] { 1f, -1f })
            {
                var bow = Find(b => b.y_max > 0 == side > 0 && b.z_min >= hull.depth_m / 2f - 0.1f && b.x_min > hull.length_m * 0.85f &&
                                    b.x_min < hull.length_m * 0.9f);
                if (bow != null)
                {
                    var r = bow.Plan.Record;
                    Marker(bow, name, new Vector3(r.x_min + 7f, side * (Mathf.Abs(side > 0 ? r.y_max : r.y_min) + 0.06f), hull.depth_m - 3.2f), side, 1.8f);
                }

                foreach (var x in new[] { hull.length_m * 0.86f, hull.length_m * 0.5f, hull.length_m * 0.22f })
                {
                    for (var z = 4; z <= Mathf.FloorToInt(hull.draft_m) + 1; z += 2)
                    {
                        var shell = Find(b => b.y_max > 0 == side > 0 && Mathf.Abs(side > 0 ? b.y_max : b.y_min) > halfBeam - 0.2f &&
                                              b.x_min <= x && x <= b.x_max && b.z_min <= z && z <= b.z_max);
                        if (shell != null)
                        {
                            Marker(shell, z + "M", new Vector3(x, side * (halfBeam + 0.06f), z), side, 0.7f);
                        }
                    }
                }
            }

            var transom = Find(b => b.y_max > 0 && b.x_min <= 0.01f && b.z_min >= hull.depth_m / 2f - 0.1f);
            if (transom != null)
            {
                Marker(transom, name + "\nGEOJE", new Vector3(-0.08f, 0f, hull.depth_m - 4.5f), 0f, 1.5f);
            }
        }

        /// <summary>Depth-tested text material (Resources Shaders/Text3D); the font's own material draws through the hull.</summary>
        private Material TextMaterial(Font font)
        {
            if (_textMaterial == null)
            {
                var shader = Shader.Find("ShipyardTwin/Text3D");
                _textMaterial = shader != null ? new Material(shader) { mainTexture = font.material.mainTexture } : font.material;
            }

            return _textMaterial;
        }

        /// <summary>White painted text at a ship-coordinate point on a block; side +1 port, -1 starboard, 0 transom.</summary>
        private void Marker(Actor block, string text, Vector3 ship, float side, float height)
        {
            var go = new GameObject("Marking " + text.Replace('\n', ' '));
            go.transform.SetParent(block.Transform, false);
            // Ship point relative to the block's centre of gravity, in the block's (ship) axes.
            go.transform.localPosition = ShipCoordinates.ShipToUnity(ship.x, ship.y, ship.z) - ShipCoordinates.CentreOfGravity(block.Plan.Record);
            // TextMesh reads from -z: starboard as is, port turned around, transom turned to face aft (-x).
            go.transform.localRotation = side < 0 ? Quaternion.identity : side > 0 ? Quaternion.Euler(0, 180f, 0) : Quaternion.Euler(0, 90f, 0);
            var mesh = go.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            mesh.fontSize = 96;
            mesh.characterSize = height / 9.6f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = new Color(0.96f, 0.96f, 0.94f);
            go.GetComponent<MeshRenderer>().sharedMaterial = TextMaterial(mesh.font);
            go.SetActive(block.Shown);
            block.Markers.Add(go);
        }

        private static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private sealed class Actor
        {
            public BlockPlan Plan;
            public HullPlan Hull;
            public GameObject Go;
            public Transform Transform;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public BoxCollider Collider;
            public HoverTarget Target;
            public Mesh Mesh;
            public bool Queued;
            public Color[] Colors = new Color[0];
            public bool Tinted;
            public bool Shown;
            public bool Hooked;
            public BlockState State = (BlockState)(-1);
            public StockStay Stay;
            public Pose Landed;
            public ErectionSchedule.Entry Entry;
            public readonly List<GameObject> Markers = new List<GameObject>();
        }
    }
}
