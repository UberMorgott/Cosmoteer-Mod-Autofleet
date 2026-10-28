using System;
using System.Collections.Generic;
using Cosmoteer;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Generators.Simulation;
using Cosmoteer.Resources;
using Cosmoteer.Ships;
using Cosmoteer.Ships.Crew.Jobs;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Resources;
using Cosmoteer.Simulation;
using Halfling.Geometry;
using Stasis = Cosmoteer.Simulation.Stasis;

namespace MiningDrone;

/// <summary>
/// Read-only world queries plus the one resource-transfer helper. Everything here is called from the
/// throttled logic tick (2-4 Hz), never per frame, and reuses buffers instead of allocating.
/// </summary>
internal static partial class World
{
    /// <summary>Safety margin (world units) added around avoidable doodads (sun kill/heat zone) when picking targets.</summary>
    public const float HazardMargin = 600f;

    private static ResourceRules[] _resources = Array.Empty<ResourceRules>();
    private static readonly Dictionary<ID<ResourceRules>, int> ResourceIndex = new();

    // Per-asteroid cache: bitmask of resource indices its parts drop, recomputed only when its part count changes.
    // Weak keys: despawned/stasis rocks drop out with their Ship instead of living until the next jump.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ship, System.Runtime.CompilerServices.StrongBox<(int PartCount, ulong Mask)>> AsteroidMasks = new();
    private static ulong _seenAsteroidMask;
    private static ulong _seenWreckMask;
    private static SimRoot? _cacheSim;

    // Reused buffers (single-threaded: everything runs on the game's main thread).
    private static readonly List<(IResourceSource Source, int AnticipatedResources, float Distance)> Sources = new();
    private static readonly List<(IResourceSink Sink, int AnticipatedCapacity, float Distance)> Sinks = new();
    private static readonly Dictionary<IResourceSink, int> Scheduled = new();
    private static readonly List<Ship> ShipBuffer = new();

    public static ResourceRules[] Resources => _resources;

    /// <summary>Bitmask (resource index) of every resource type seen on any asteroid so far.</summary>
    public static ulong SeenAsteroidMask => _seenAsteroidMask;

    /// <summary>Bitmask of resource types seen on derelict wrecks so far.</summary>
    public static ulong SeenWreckMask => _seenWreckMask;

    /// <summary>Derelict hulk: owned by the junk player (Ship.IsJunk: PlayerIndex == -3), not an asteroid. Never a player or live NPC ship.</summary>
    public static bool IsWreck(Ship s) => s.IsJunk && !s.Rules.IsAsteroid;

    public static void EnsureResources()
    {
        ResourceRules[] all = GameApp.Rules.Resources;
        if (ReferenceEquals(all, _resources))
        {
            return;
        }
        _resources = all;
        ResourceIndex.Clear();
        if (all.Length > 64)
        {
            Log.Info($"{all.Length} resource types; only the first 64 can be filtered/tracked (bitmask)");
        }
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            ResourceIndex[all[i].ID] = i;
        }
    }

    public static int IndexOf(ID<ResourceRules> id) => ResourceIndex.TryGetValue(id, out int i) ? i : -1;

    public static ulong MaskFromIds(List<string> ids)
    {
        EnsureResources();
        ulong mask = 0;
        foreach (string s in ids)
        {
            for (int i = 0; i < _resources.Length && i < 64; i++)
            {
                if (_resources[i].ID.ToString() == s)
                {
                    mask |= 1UL << i;
                }
            }
        }
        return mask;
    }

    private static readonly ID<SimObjectSpawner> AsteroidTag = new("asteroid");
    private static ulong _asteroidGroupMask;
    private static ulong _stackableMask;
    private static ResourceRules[]? _groupsFor;

    /// <summary>Resource types asteroid parts can drop: PartRules.Resources of every ShipRules with IsAsteroid (GameApp.Rules.Ships).</summary>
    public static ulong AsteroidGroupMask
    {
        get
        {
            EnsureGroups();
            return _asteroidGroupMask;
        }
    }

    /// <summary>All stackable (collectable) resource types = the default "everything selected".</summary>
    public static ulong StackableMask
    {
        get
        {
            EnsureGroups();
            return _stackableMask;
        }
    }

    private static void EnsureGroups()
    {
        EnsureResources();
        if (ReferenceEquals(_groupsFor, _resources))
        {
            return;
        }
        _groupsFor = _resources;
        _stackableMask = 0;
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            if (_resources[i].IsStackable)
            {
                _stackableMask |= 1UL << i;
            }
        }
        _asteroidGroupMask = 0;
        foreach (ShipRules sr in GameApp.Rules.Ships)
        {
            if (sr == null || !sr.IsAsteroid || sr.Parts == null)
            {
                continue;
            }
            foreach (PartRules pr in sr.Parts)
            {
                var res = pr?.Resources;
                if (res == null)
                {
                    continue;
                }
                for (int r = 0; r < res.Length; r++)
                {
                    int idx = IndexOf(res[r].Type);
                    if (idx >= 0)
                    {
                        _asteroidGroupMask |= 1UL << idx;
                    }
                }
            }
        }
        _asteroidGroupMask &= _stackableMask;
    }


    /// <summary>
    /// Asteroids far from every player ship are not Ships at all: they sit in stasis as StasisDoodadSpawners and
    /// only spawn within SimRules.StasisLiveRange (2500) of a root location (SimStasisManager.cs:1137).
    /// Returns the location of the nearest asteroid spawner whose doodad ID names a selected asteroid resource
    /// (e.g. "cosmoteer.asteroid_iron_m"), outside sun hazards, inside the area (Area mode) or maxDist.
    /// </summary>
    public readonly record struct DiagSnap(int LiveAsteroids, int LiveWrecks, int StasisSeen, int NuggetsSeen, int Excluded, int Empty,
        int Filter, int NotSalvageable, int Sun, int OutOfArea, int Undecoded, int Threat);

    /// <summary>Per-search rejection counters (reset by the caller before a search; read when nothing was found).</summary>
    public static class Diag
    {
        public static int LiveAsteroids, LiveWrecks, StasisSeen, NuggetsSeen;
        public static int Excluded, Empty, Filter, NotSalvageable, Sun, OutOfArea, Undecoded, Threat;

        public static void Reset()
        {
            LiveAsteroids = LiveWrecks = StasisSeen = NuggetsSeen = 0;
            Excluded = Empty = Filter = NotSalvageable = Sun = OutOfArea = Undecoded = Threat = 0;
        }

        /// <summary>Counters before a search that resets them internally (the doubling FindTarget), to add back after.</summary>
        public static DiagSnap Save() => new(LiveAsteroids, LiveWrecks, StasisSeen, NuggetsSeen, Excluded, Empty, Filter, NotSalvageable, Sun, OutOfArea, Undecoded, Threat);

        public static void Add(in DiagSnap s)
        {
            LiveAsteroids += s.LiveAsteroids;
            LiveWrecks += s.LiveWrecks;
            StasisSeen += s.StasisSeen;
            NuggetsSeen += s.NuggetsSeen;
            Excluded += s.Excluded;
            Empty += s.Empty;
            Filter += s.Filter;
            NotSalvageable += s.NotSalvageable;
            Sun += s.Sun;
            OutOfArea += s.OutOfArea;
            Undecoded += s.Undecoded;
            Threat += s.Threat;
        }

        public static void Seen(bool asteroid)
        {
            if (asteroid) LiveAsteroids++; else LiveWrecks++;
        }

        public static string Summary() =>
            $"live asteroids {LiveAsteroids}, live wrecks {LiveWrecks}, stasis records {StasisSeen}, loose {NuggetsSeen}; rejected: empty {Empty}, filter {Filter}, sun/hidden {Sun}, outside area/range {OutOfArea}, blacklisted/claimed {Excluded}, not salvageable {NotSalvageable}, in enemy range {Threat}; undecoded(maybe) {Undecoded}";
    }

    // ---- stasis yield index (pure save-derived data) ----
    // An asteroid that left the live range in an earlier session is stored in the save as gzip'd ObjectBits
    // (StasisDoodadSpawner.ObjectData, written in OnEnteringStasis, StasisDoodadSpawner.cs:103-121). The game
    // decodes it with GeneratedShipDoodadRules.CreateShip: DecompressGzip -> OBNode -> ObjectBitsSerializer.Read<Ship>
    // (GeneratedShipDoodadRules.cs, also run off-sim by Preload and simply disposed by CancelPreload). We do exactly
    // that, read-only: the Ship is never added to the sim, uses its own serialized IDs/Rand, and is disposed after
    // counting resource parts. Done once per sim attach, incrementally on the main thread under a per-frame budget.
    // Keyed by the payload array, not the spawner: a StasisShipSpawner is reused across stasis cycles with a new
    // ShipData each time (StasisHandler.OnEnteringStasis), so only the payload identity can never go stale.
    // Junk ship records (wrecks, caches, derelicts) go through the same queue: value = (yields, is asteroid).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], object> StasisYields = new();
    private static readonly Queue<Stasis.StasisSpawner> DecodeQueue = new();
    private static readonly HashSet<Stasis.StasisSpawner> Queued = new();
    private static System.Threading.Tasks.Task<(ulong Yields, bool Asteroid)>? _decodeTask;
    private static Stasis.StasisSpawner? _decoding;
    private static byte[]? _decodingData;
    private static System.Diagnostics.Stopwatch? _decodeClock;
    private static int _decoded;
    private static double _decodeMsTotal;
    private static double _decodeMsMax;
    private static bool _decodeLogged = true;

    /// <summary>Called on sim attach: queue every asteroid stasis record that carries saved (modified) ship data.</summary>
    public static void StartStasisIndex(SimRoot sim)
    {
        StasisYields.Clear();
        DecodeQueue.Clear();
        Queued.Clear();
        _decodeTask = null;
        _decoding = null;
        _decoded = 0;
        _decodeMsTotal = 0;
        _decodeMsMax = 0;
        int ships = 0;
        foreach (var spawner in sim.Stasis)
        {
            bool rock = spawner is Stasis.StasisDoodadSpawner ds && IsAsteroidSpawner(ds);
            if ((rock || IsJunkShipRecord(spawner)) && HasSavedData(spawner))
            {
                EnqueueDecode(spawner);
                ships += rock ? 0 : 1;
            }
        }
        _decodeLogged = false;
        Log.Info($"Stasis index: {DecodeQueue.Count - ships} modified asteroid records and {ships} junk ship records queued for decoding");
    }

    private static void EnqueueDecode(Stasis.StasisSpawner s)
    {
        if (SavedData(s) is byte[] data && StasisYields.TryGetValue(data, out _))
        {
            return;
        }
        if (Queued.Add(s))
        {
            DecodeQueue.Enqueue(s);
            _decodeLogged = false;
        }
    }

    private static bool IsAsteroidSpawner(Stasis.StasisDoodadSpawner ds) =>
        ds.DoodadRules?.Tags != null && ds.DoodadRules.Tags.Contains(AsteroidTag);

    /// <summary>Saved (modified) payload of a rock or ship stasis record; still-serializing or in-sim (empty) payloads count as none.</summary>
    private static System.Threading.Tasks.Task<byte[]>? DataTask(Stasis.StasisSpawner s) => s switch
    {
        Stasis.StasisDoodadSpawner ds => ds._asyncObjectDataTask,
        Stasis.StasisShipSpawner ss => ss._asyncShipDataTask!,
        Stasis.SimStasisManager.SerializedStasisShip st => st._asyncShipDataTask,
        _ => NoData,
    };

    private static readonly System.Threading.Tasks.Task<byte[]> NoData = System.Threading.Tasks.Task.FromResult(Array.Empty<byte>());

    private static byte[]? SavedData(Stasis.StasisSpawner s) =>
        DataTask(s) is { IsCompletedSuccessfully: true, Result: { Length: > 0 } data } ? data : null;

    private static bool HasSavedData(Stasis.StasisSpawner s) => SavedData(s) != null;

    /// <summary>The spawner's payload is still being serialized (OnEnteringStasis runs it async): contents unknown yet.</summary>
    private static bool SavedDataPending(Stasis.StasisSpawner s) => DataTask(s) is { IsCompleted: false };

    private static bool TryGetYields(Stasis.StasisSpawner s, out ulong yields) => TryGetDecoded(s, out yields, out _);

    private static bool TryGetDecoded(Stasis.StasisSpawner s, out ulong yields, out bool asteroid)
    {
        if (SavedData(s) is byte[] data && StasisYields.TryGetValue(data, out object? boxed))
        {
            (yields, asteroid) = ((ulong, bool))boxed;
            return true;
        }
        yields = 0;
        asteroid = false;
        return false;
    }

    /// <summary>
    /// Advances decoding; called every frame. Each record is decoded on the game's own SingleThreadTaskQueue (the
    /// same serialized worker vanilla uses for GeneratedShipDoodadRules.CreateShip / StartAsyncSpawn), one at a time,
    /// so the game thread never blocks on a large record; it only polls the finished task.
    /// </summary>
    public static void DecodeStasis(GameRoot game)
    {
        if (_decodeTask != null)
        {
            if (!_decodeTask.IsCompleted)
            {
                return;
            }
            double ms = _decodeClock?.Elapsed.TotalMilliseconds ?? 0;
            if (_decodeTask.IsCompletedSuccessfully && _decodingData != null)
            {
                StasisYields.AddOrUpdate(_decodingData, _decodeTask.Result);
                _decoded++;
                _decodeMsTotal += ms;
                _decodeMsMax = Math.Max(_decodeMsMax, ms);
            }
            else if (_decodeTask.Exception != null)
            {
                Log.Error("stasis-decode", _decodeTask.Exception);
            }
            if (_decoding != null)
            {
                Queued.Remove(_decoding);
            }
            _decodeTask = null;
            _decoding = null;
            _decodingData = null;
        }
        if (DecodeQueue.Count == 0)
        {
            if (!_decodeLogged)
            {
                _decodeLogged = true;
                Log.Info($"Stasis index: decoded {_decoded} records, worker time {_decodeMsTotal:0} ms total (avg {(_decoded > 0 ? _decodeMsTotal / _decoded : 0):0.0} ms, max {_decodeMsMax:0.0} ms), off the game thread");
            }
            return;
        }
        var ds = DecodeQueue.Dequeue(); // stays in Queued until its worker task completes (in-flight dedup)
        byte[]? data = SavedData(ds);
        if (data == null || StasisYields.TryGetValue(data, out _))
        {
            Queued.Remove(ds);
            return;
        }
        _decoding = ds;
        _decodingData = data;
        _decodeClock = System.Diagnostics.Stopwatch.StartNew();
        _decodeTask = game.SingleThreadTaskQueue.Run(_ =>
        {
            using var raw = new System.IO.MemoryStream(data);
            using var unzipped = raw.DecompressGzip();
            var node = new Halfling.ObjectBits.OBNode(unzipped);
            Ship ship = Halfling.App.ObjectBitsSerializer.Read<Ship>(node);
            bool asteroid = ship.Rules.IsAsteroid;
            // Stored totals are kept by part attach/detach (ResourceStorage.OnPartAttached2), so they hold off-sim too.
            ulong yields = ComputeMask(ship) | (asteroid ? 0 : CargoMask(ship));
            lock (ship)
            {
                ship.Dispose();
            }
            return (yields, asteroid);
        });
    }
    /// <summary>World position of the nearest part on <paramref name="target"/> that drops or stores a wanted resource
    /// (stored cargo spills when its part is salvaged: the same storages SalvageCommand checks via
    /// ShipMetadata.PartProducesUnbannedResourceType).</summary>
    public static Vector2? NearestYieldPart(Ship target, ulong want, Vector2 from)
    {
        Vector2? best = null;
        float bestD2 = float.MaxValue;
        ulong cargo = IsWreck(target) ? CargoMask(target) & want : 0;
        int count = target.Parts.Count;
        for (int p = 0; p < count; p++)
        {
            var part = target.Parts[p];
            var res = part.Rules.Resources;
            bool wanted = false;
            for (int r = 0; res != null && r < res.Length && !wanted; r++)
            {
                int idx = IndexOf(res[r].Type);
                wanted = idx >= 0 && (want & (1UL << idx)) != 0;
            }
            if (!wanted && cargo != 0)
            {
                wanted = PartStores(part, cargo);
            }
            if (!wanted)
            {
                continue;
            }
            Vector2 c = part.WorldCenter;
            float d2 = c.DistanceSquaredTo(from);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = c;
            }
        }
        return best;
    }

    private static bool PartStores(Part part, ulong mask)
    {
        for (ulong m = mask; m != 0; m &= m - 1)
        {
            int i = System.Numerics.BitOperations.TrailingZeroCount(m);
            foreach (var s in Cosmoteer.Ships.Parts.Resources.BaseResourceStorage.GetAllSimpleStoragesOfTypeOnPart(_resources[i].ID, part))
            {
                if (s.Resources > 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Stackable resource types stored aboard (salvage spills them as nuggets); crew-carried ones excluded.
    /// Read fresh: cargo changes without the part count changing.</summary>
    public static ulong CargoMask(Ship ship)
    {
        ulong mask = 0;
        ResourceManager rm = ship.Resources;
        ResourceRules[] all = _resources;
        for (int i = 0; i < all.Length && i < 64; i++)
        {
            if (all[i].IsStackable && rm.GetResourceTotal(all[i].ID, includeCarried: false) > 0)
            {
                mask |= 1UL << i;
            }
        }
        return mask;
    }

    /// <summary>What salvaging this body can yield: hull part drops, plus stored cargo on a junk ship (wreck, cache, derelict).</summary>
    public static ulong YieldOf(Ship s) => GetAsteroidMask(s) | (IsWreck(s) ? CargoMask(s) : 0);

    // ------------------------------------------------------------------ junk ship kinds (Loot sources)

    private const int JunkPlayer = -3; // Ship.IsJunk: PlayerIndex == -3
    private const string AbandonedLabel = "abandoned"; // sysgen_misc_discoverables.rules "Abandoned Ship": Labels = ["abandoned"]
    private static readonly ID<SimObjectSpawner> StoragePodTag = new("storage_pod"); // spawner Tags of the storage pod
    private static readonly ID<BuiltinShipTag> StoragePodShipTag = new("storage_pod"); // builtins_misc.rules: Small Storage Pod

    /// <summary>Live junk ship kind. Labels are not saved (Ship.s_labels is a weak table), but the spawner held by the
    /// stasis handler is (StasisHandler.OriginalSpawner), so a derelict stays one across save/load.</summary>
    public static JunkKind Classify(Ship s)
    {
        if (!IsWreck(s))
        {
            return JunkKind.None;
        }
        Stasis.StasisShipSpawner? sp = (s.SpecialStasisHandler as Stasis.StasisShipSpawner.StasisHandler)?.OriginalSpawner;
        bool abandoned = s.HasLabel(AbandonedLabel) || sp?.Labels?.Contains(AbandonedLabel) == true;
        return KindOf(true, abandoned, IsPod(s.Tags) || (sp != null && IsPodSpawner(sp)));
    }

    private static bool IsPod(HashSet<ID<SimObjectSpawner>>? tags) => tags != null && tags.Contains(StoragePodTag);

    private static bool IsPodSpawner(Stasis.StasisShipSpawner sp) =>
        IsPod(sp.Tags) || (GameApp.Rules.BuiltinShips.TryGetShip(sp.ShipID, out var b) && b.Tags != null && b.Tags.Contains(StoragePodShipTag));

    private static bool IsJunkShipRecord(Stasis.StasisSpawner s) =>
        s is Stasis.StasisShipSpawner or Stasis.SimStasisManager.SerializedStasisShip && s.ObjectAllegiance == JunkPlayer;

    private static Ship? Held(System.Threading.Tasks.Task<Ship>? t) => t is { IsCompletedSuccessfully: true } ? t.Result : null;

    /// <summary>What a junk ship stasis record is and what it is known to yield. false = not a junk ship (asteroid
    /// fragment, live NPC) or a saved record not decoded yet (could be a rock fragment: queued, skipped meanwhile).
    /// Known yield: the held Ship (this-session stasis or preload) or the decoded saved payload = complete; else the
    /// spawner's ResourcesMemory (unmodified ship) = cargo only; a pristine spawner = nothing known.</summary>
    private static bool JunkShipInfo(Stasis.StasisSpawner spawner, out JunkKind kind, out ulong known, out bool complete)
    {
        kind = JunkKind.None;
        known = 0;
        complete = false;
        if (!IsJunkShipRecord(spawner))
        {
            return false;
        }
        Ship? held;
        Stasis.StasisShipSpawner? ss = spawner as Stasis.StasisShipSpawner;
        if (ss != null)
        {
            held = Held(ss._asyncSpawnTask);
            kind = KindOf(true, ss.Labels?.Contains(AbandonedLabel) == true || held?.HasLabel(AbandonedLabel) == true, IsPodSpawner(ss));
        }
        else
        {
            var st = (Stasis.SimStasisManager.SerializedStasisShip)spawner;
            held = Held(st._asyncSpawnTask!);
            kind = KindOf(true, held?.HasLabel(AbandonedLabel) == true, IsPod(st.Tags));
        }
        if (held != null)
        {
            if (held.Rules.IsAsteroid)
            {
                return false;
            }
            known = ComputeMask(held) | CargoMask(held);
            complete = true;
            return true;
        }
        if (TryGetDecoded(spawner, out ulong y, out bool asteroid))
        {
            known = y;
            complete = true;
            return !asteroid;
        }
        if (HasSavedData(spawner) || SavedDataPending(spawner))
        {
            if (HasSavedData(spawner))
            {
                EnqueueDecode(spawner);
            }
            Diag.Undecoded++;
            return false;
        }
        if (ss == null)
        {
            return false; // serialized record without payload: nothing to go on
        }
        if (ss.ResourcesMemory is { } mem)
        {
            MemTotals.Clear();
            MemCaps.Clear();
            mem.GetTotalResources(MemTotals, MemCaps);
            foreach (var (id, n) in MemTotals)
            {
                if (n > 0 && IndexOf(id) is int i and >= 0)
                {
                    known |= 1UL << i;
                }
            }
        }
        return true;
    }

    private static readonly Dictionary<ID<ResourceRules>, int> MemTotals = new(), MemCaps = new();

    /// <summary>
    /// Nearest junk ship in stasis (wreck / cache / derelict, per <paramref name="kinds"/>) with a wanted yield, same
    /// filters and stasis bias as <see cref="FindStasisAsteroid"/>. A confirmed pick always wins; otherwise the nearest
    /// unknown one is returned with <paramref name="maybe"/> set (the caller takes it only when Loot has nothing else).
    /// </summary>
    public static Vector2? FindStasisJunk(SimRoot sim, Vector2 origin, bool areaMode, in Circle area, float maxDist, ulong want, int kinds,
        Dictionary<Vector2, long>? skip, long nowMs, byte[]? ranks, out double cost, out bool maybe, out JunkKind pickKind)
    {
        cost = double.MaxValue;
        maybe = false;
        pickKind = JunkKind.None;
        JunkKind bestKind = JunkKind.None, maybeKind = JunkKind.None;
        if (want == 0 || kinds == 0)
        {
            return null;
        }
        Vector2? best = null, maybeLoc = null;
        double bestCost = double.MaxValue;
        float maybeD2 = float.MaxValue;
        foreach (var spawner in sim.Stasis)
        {
            if (!JunkShipInfo(spawner, out JunkKind kind, out ulong known, out bool complete) || (kinds & KindBit(kind)) == 0)
            {
                continue;
            }
            Vector2 loc = spawner.Location;
            Diag.StasisSeen++;
            if (skip != null && skip.TryGetValue(loc, out long until) && until > nowMs)
            {
                Diag.Excluded++;
                continue;
            }
            float d2 = loc.DistanceSquaredTo(origin);
            if (areaMode ? !area.Contains(loc) : d2 > maxDist * maxDist)
            {
                Diag.OutOfArea++;
                continue;
            }
            if (IsHazardous(sim, new Circle(loc, 300f)))
            {
                Diag.Sun++;
                continue;
            }
            if (ThreatZones.Count > 0 && InThreat(loc))
            {
                Diag.Threat++;
                continue;
            }
            switch (StasisVerdict(known, complete, want))
            {
                case Verdict.Reject:
                    if (known == 0) Diag.Empty++; else Diag.Filter++;
                    continue;
                case Verdict.Maybe:
                    if (d2 < maybeD2)
                    {
                        maybeLoc = loc;
                        maybeD2 = d2;
                        maybeKind = kind;
                    }
                    continue;
            }
            double c = CostOf(d2, known & want, ranks, StasisBias);
            if (c < bestCost)
            {
                best = loc;
                bestCost = c;
                bestKind = kind;
            }
        }
        if (best != null)
        {
            cost = bestCost;
            pickKind = bestKind;
            return best;
        }
        if (maybeLoc != null)
        {
            maybe = true;
            pickKind = maybeKind;
            cost = CostOf(maybeD2, 0, ranks, StasisBias); // unknown yield: last rank
        }
        return maybeLoc;
    }

    private static ulong ComputeMask(Ship ship)
    {
        ulong mask = 0;
        int count = ship.Parts.Count;
        for (int p = 0; p < count; p++)
        {
            var res = ship.Parts[p].Rules.Resources;
            if (res == null)
            {
                continue;
            }
            for (int r = 0; r < res.Length; r++)
            {
                int idx = IndexOf(res[r].Type);
                if (idx >= 0)
                {
                    mask |= 1UL << idx;
                }
            }
        }
        return mask;
    }
    /// <summary>Last known yield mask per asteroid ObjectID (live observation; survives the rock entering stasis).</summary>
    private static readonly Dictionary<ObjectID, ulong> KnownYield = new();

    // Per-search counts by yield source (for the candidate log line).
    private static int _cLive, _cCarried, _cDecoded, _cPristine, _cMaybe;
    private static float _bestKnownD = -1f, _bestMaybeD = -1f;

    /// <summary>Summary of the last stasis search: candidates per yield source and the nearest known / maybe.</summary>
    public static string StasisCandidates() =>
        $"stasis candidates: live {_cLive}, carried {_cCarried}, decoded {_cDecoded}, pristine {_cPristine}, maybe {_cMaybe}; nearest known {(_bestKnownD < 0 ? "-" : _bestKnownD.ToString("0"))}, nearest maybe {(_bestMaybeD < 0 ? "-" : _bestMaybeD.ToString("0"))}";

    /// <summary>
    /// Nearest stasis asteroid with a wanted resource. Yield sources, most certain first: the live Ship still held by
    /// the spawner (live), this spawner's decoded save record (decoded), the last live observation carried by ObjectID
    /// (carried), the untouched doodad ID (pristine). A modified record not decoded yet, or still being serialized, is
    /// only a "maybe": it is chosen only when no known-good candidate exists. <paramref name="source"/> names the source of the returned pick.
    /// </summary>
    public static Vector2? FindStasisAsteroid(SimRoot sim, Vector2 origin, bool areaMode, in Circle area, float maxDist, ulong asteroidMask,
        Dictionary<Vector2, long>? skip, long nowMs, byte[]? ranks, out double cost, out string source)
    {
        source = "";
        cost = double.MaxValue;
        double bestCost = double.MaxValue; // without ranks = bestD2
        _cLive = _cCarried = _cDecoded = _cPristine = _cMaybe = 0;
        _bestKnownD = _bestMaybeD = -1f;
        if (asteroidMask == 0)
        {
            return null;
        }
        Vector2? best = null;
        float bestD2 = float.MaxValue;
        string bestSrc = "";
        Vector2? maybe = null;
        float maybeD2 = float.MaxValue;
        foreach (var spawner in sim.Stasis)
        {
            if (spawner is not Cosmoteer.Simulation.Stasis.StasisDoodadSpawner ds || ds.DoodadRules?.Tags == null || !ds.DoodadRules.Tags.Contains(AsteroidTag))
            {
                continue;
            }
            Vector2 loc = ds.Location;
            Diag.StasisSeen++;
            if (skip != null && skip.TryGetValue(loc, out long until) && until > nowMs)
            {
                Diag.Excluded++;
                continue;
            }
            if (areaMode && !area.Contains(loc))
            {
                Diag.OutOfArea++;
                continue;
            }
            float d2 = loc.DistanceSquaredTo(origin);
            if (!areaMode && d2 > maxDist * maxDist)
            {
                Diag.OutOfArea++;
                continue;
            }
            if (IsHazardous(sim, new Circle(loc, 300f)))
            {
                Diag.Sun++;
                continue;
            }
            if (ThreatZones.Count > 0 && InThreat(loc))
            {
                Diag.Threat++;
                continue;
            }
            // Emptiness without visiting: an asteroid that went into stasis this session keeps its Ship object in
            // the spawner (OnEnteringStasis: _asyncSpawnTask = Task.FromResult(ship), StasisDoodadSpawner.cs:90), so
            // its remaining parts are known exactly. CancelPreload (StasisDoodadSpawner.cs:364) drops that Ship; the
            // spawner keeps the rock's ObjectID (ReservedObjectID), so the last live observation is carried over.
            // Otherwise a never-touched rock yields what its doodad ID says; a modified one (serialized ObjectData)
            // uses the decoded save record (StasisYields). Only an undecoded modified record is unknown ("maybe").
            if (CostOf(d2, 0, ranks == null ? null : ZeroRanks, StasisBias) >= bestCost && d2 >= maybeD2)
            {
                continue; // can't beat either pick (rank 0 = lower bound of the priority cost)
            }
            ulong y;
            string src;
            var spawned = ds._asyncSpawnTask;
            if (spawned != null && spawned.IsCompletedSuccessfully && spawned.Result is Ship sh)
            {
                y = GetAsteroidMask(sh);
                KnownYield[ds.ReservedObjectID] = y;
                src = "live";
            }
            else if (HasSavedData(ds) && TryGetYields(ds, out y))
            {
                src = "decoded"; // the current spawner's own saved payload beats an older live observation
            }
            else if (KnownYield.TryGetValue(ds.ReservedObjectID, out y))
            {
                src = "carried";
            }
            else if (HasSavedData(ds) || SavedDataPending(ds))
            {
                // Modified rock whose payload is not decoded yet (or still being serialized): unknown, not pristine.
                Diag.Undecoded++;
                _cMaybe++;
                if (HasSavedData(ds))
                {
                    EnqueueDecode(ds); // e.g. a rock that entered stasis in an earlier session and was preload-cancelled
                }
                if (d2 < maybeD2)
                {
                    maybe = loc;
                    maybeD2 = d2;
                }
                continue;
            }
            else
            {
                y = DoodadSelectedResources(ds.DoodadRules.ID.ToString(), asteroidMask); // only the types its ID names
                if (y == 0)
                {
                    Diag.Filter++;
                    continue;
                }
                src = "pristine";
            }
            if ((y & asteroidMask) == 0)
            {
                if (y == 0) Diag.Empty++; else Diag.Filter++;
                continue;
            }
            switch (src)
            {
                case "live": _cLive++; break;
                case "carried": _cCarried++; break;
                case "decoded": _cDecoded++; break;
                default: _cPristine++; break;
            }
            double c = CostOf(d2, y & asteroidMask, ranks, StasisBias);
            if (c >= bestCost)
            {
                continue;
            }
            best = loc;
            bestCost = c;
            bestD2 = d2;
            bestSrc = src;
        }
        if (best != null)
        {
            _bestKnownD = (float)Math.Sqrt(bestD2);
        }
        if (maybe != null)
        {
            _bestMaybeD = (float)Math.Sqrt(maybeD2);
        }
        if (best != null)
        {
            source = bestSrc; // known-good candidates always win over "maybe" ones
            cost = bestCost;
            return best;
        }
        source = maybe != null ? "maybe" : "";
        if (maybe != null)
        {
            cost = CostOf(maybeD2, 0, ranks, StasisBias); // unknown yield: last rank
        }
        return maybe;
    }

    private static readonly byte[] ZeroRanks = new byte[65];

    private static ulong DoodadSelectedResources(string doodadId, ulong mask)
    {
        ulong found = 0;
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0)
            {
                continue;
            }
            string rid = _resources[i].ID.ToString();
            int dot = rid.IndexOf('.');
            string shortName = dot >= 0 ? rid.Substring(dot + 1) : rid;
            if (doodadId.Contains("_" + shortName + "_", StringComparison.Ordinal) || doodadId.EndsWith("_" + shortName, StringComparison.Ordinal))
            {
                found |= 1UL << i;
            }
        }
        return found;
    }

    public static void ResetCachesIfSimChanged(SimRoot sim)
    {
        if (!ReferenceEquals(sim, _cacheSim))
        {
            _cacheSim = sim;
            AsteroidMasks.Clear();
            KnownYield.Clear();
        }
    }

    /// <summary>Resource types an asteroid can drop (from PartRules.Resources), cached by part count.</summary>
    public static ulong GetAsteroidMask(Ship asteroid)
    {
        int count = asteroid.Parts.Count;
        if (AsteroidMasks.TryGetValue(asteroid, out var cached) && cached.Value.PartCount == count)
        {
            return cached.Value.Mask;
        }
        ulong mask = 0;
        for (int p = 0; p < count; p++)
        {
            Part part = asteroid.Parts[p];
            var res = part.Rules.Resources;
            if (res == null)
            {
                continue;
            }
            for (int r = 0; r < res.Length; r++)
            {
                int idx = IndexOf(res[r].Type);
                if (idx >= 0)
                {
                    mask |= 1UL << idx;
                }
            }
        }
        AsteroidMasks.AddOrUpdate(asteroid, new((count, mask)));
        if (asteroid.Rules.IsAsteroid)
        {
            _seenAsteroidMask |= mask;
            // Carried across stasis: the spawner that replaces this rock keeps its ObjectID (ReservedObjectID,
            // StasisDoodadSpawner.OnEnteringStasis), so its remaining yield stays known after CancelPreload drops the
            // live Ship and before the saved payload is decoded.
            KnownYield[asteroid.UniqueID] = mask;
        }
        else
        {
            _seenWreckMask |= mask;
        }
        return mask;
    }

    /// <summary>One cheap pass over all ships to learn which resource types asteroids in this sector carry (for the UI filter list).</summary>
    public static void PrimeAsteroidMasks(SimRoot sim)
    {
        ResetCachesIfSimChanged(sim);
        SimShipsManager ships = sim.Ships;
        for (int i = 0; i < ships.Count; i++)
        {
            Ship s = ships[i];
            if (s.Rules.IsAsteroid || IsWreck(s))
            {
                GetAsteroidMask(s);
            }
        }
    }

    /// <summary>True if the circle overlaps a sun (or other avoidable doodad tagged for commands) inflated by the margin.</summary>
    public static bool IsHazardous(SimRoot sim, in Circle c)
    {
        return sim.Doodads.ShouldAvoidLocation(c, GameApp.Rules.Commands.Salvage.AvoidableDoodadTags, HazardMargin);
    }

    private const float FirstSearchRadius = 4000f;
    private static readonly List<Ship> QueryBuffer = new();

    // ------------------------------------------------------------------ collect priority (D18)

    /// <summary>Rank per resource index (dense over the listed known types in <paramref name="mask"/>, in list order);
    /// entry 64 = the shared last rank of unlisted types. null when no priority is set (nearest-target behaviour).</summary>
    public static byte[]? RankTable(List<string>? order, ulong mask)
    {
        if (order == null || order.Count == 0)
        {
            return null;
        }
        EnsureResources();
        var idx = new int[order.Count];
        for (int i = 0; i < order.Count; i++)
        {
            idx[i] = -1;
            for (int t = 0; t < _resources.Length && t < 64; t++)
            {
                if (_resources[t].ID.ToString() == order[i])
                {
                    idx[i] = t;
                    break;
                }
            }
        }
        return RankTable(idx, mask);
    }

    /// <summary>Offline invariants of the collect priority (D18), run by tools/jitcheck. null = pass.</summary>
    internal static string? SelfCheck()
    {
        if (PrioritySelfCheck() is string pure) return pure;
        // A rank-0 body overlapping the area from outside must not count; the inner one does; legacy unchanged.
        byte[] rk = RankTable(new[] { 5, -1, 5, 9, 2 }, (1UL << 5) | (1UL << 2) | (1UL << 7));
        var areaC = new Circle(new Vector2(0f, 0f), 3000f);
        if (Counts(rk, areaC, new Vector2(3200f, 0f)) || !Counts(rk, areaC, new Vector2(1000f, 0f)) || !Counts(null, areaC, new Vector2(3200f, 0f)))
            return "priority search counts bodies centred outside the area";
        return null;
    }

    /// <summary>Priority path: only bodies centred inside the queried region count (the approximate query also returns ones
    /// that merely overlap it; a high-rank rock just outside the area would win, be dropped as outside, and be re-picked).</summary>
    internal static bool Counts(byte[]? ranks, in Circle region, Vector2 center) => ranks == null || region.Contains(center);

    /// <summary>
    /// Finds the nearest minable junk ship (asteroid and/or derelict wreck) using the spatial index
    /// (SimShipsManager.DetFindIntersectingWith, junk: true) instead of scanning the whole sector.
    /// Area mode: one query over the area circle. Nearest mode: the query radius doubles from 4 km up to
    /// <paramref name="maxDist"/> until something qualifies (search radius grows as nearby rocks deplete).
    /// </summary>
    public static Ship? FindTarget(SimRoot sim, Ship miner, Vector2 origin, bool areaMode, in Circle area, float maxDist,
        ulong asteroidMask, ulong wreckMask, HashSet<Ship> exclude, byte[]? ranks, out double cost, int kinds = 0)
    {
        ResetCachesIfSimChanged(sim);
        if (areaMode)
        {
            return QueryNearest(sim, miner, origin, area, asteroidMask, wreckMask, kinds, exclude, ranks, out cost);
        }
        float r = Math.Min(FirstSearchRadius, maxDist);
        while (true)
        {
            Ship? found = QueryNearest(sim, miner, origin, new Circle(origin, r), asteroidMask, wreckMask, kinds, exclude, ranks, out cost);
            // With a priority a farther, higher-ranked target can still win: widen until the ring bound settles it.
            if ((found != null && (ranks == null || RingDone(cost, r))) || r >= maxDist)
            {
                return found;
            }
            r = Math.Min(r * 2f, maxDist);
        }
    }

    private static Ship? QueryNearest(SimRoot sim, Ship miner, Vector2 origin, Circle region, ulong asteroidMask, ulong wreckMask, int kinds,
        HashSet<Ship> exclude, byte[]? ranks, out double bestCost)
    {
        Diag.Reset(); // the doubling search re-queries; only the last (widest) pass is reported
        QueryBuffer.Clear();
        sim.Ships.DetFindIntersectingWith(region, QueryBuffer, approximate: true, junk: true);
        Ship? best = null;
        bestCost = double.MaxValue;
        for (int i = 0; i < QueryBuffer.Count; i++)
        {
            Ship s = QueryBuffer[i];
            // Selection masks: 0 = this kind is not collected.
            bool isAst = s.Rules.IsAsteroid;
            ulong want = isAst ? asteroidMask : (wreckMask != 0 && (kinds & KindBit(Classify(s))) != 0 ? wreckMask : 0);
            if (s == miner || want == 0)
            {
                continue; // kind not collected (counted once per search as a disabled category)
            }
            Diag.Seen(isAst);
            if (exclude.Contains(s))
            {
                Diag.Excluded++;
                continue;
            }
            ulong yields = s.Parts.Count == 0 ? 0 : YieldOf(s);
            if (yields == 0)
            {
                Diag.Empty++;
                continue;
            }
            if ((yields & want) == 0)
            {
                Diag.Filter++;
                continue;
            }
            Circle bc = s.WorldBoundingCircle;
            if (!s.CanBeSalvagedByLocalPlayer(allowSalvageOwnShip: false))
            {
                Diag.NotSalvageable++;
                continue;
            }
            if (IsHazardous(sim, bc))
            {
                Diag.Sun++;
                continue;
            }
            if (ThreatZones.Count > 0 && InThreat(bc.Center))
            {
                Diag.Threat++;
                continue;
            }
            if (!Counts(ranks, region, bc.Center))
            {
                Diag.OutOfArea++;
                continue;
            }
            double c = CostOf(bc.Center.DistanceSquaredTo(origin), yields & want, ranks);
            if (c >= bestCost)
            {
                continue;
            }
            best = s;
            bestCost = c;
        }
        QueryBuffer.Clear();
        return best;
    }

    /// <summary>
    /// Types this miner offloads: everything collectable aboard, except types marked "keep all"
    /// (ShipSettings.KeepAll). Partial protection is the keep-on-board amount (see <see cref="Keep"/>).
    /// </summary>
    public static ulong OffloadMask(List<string>? keepAll)
    {
        EnsureGroups();
        if (keepAll == null || keepAll.Count == 0)
        {
            return _stackableMask;
        }
        ulong mask = _stackableMask;
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            if (keepAll.Contains(_resources[i].ID.ToString()))
            {
                mask &= ~(1UL << i);
            }
        }
        return mask;
    }

    /// <summary>Types in <paramref name="mask"/> the ship can't take one more unit of (no storage room, or banned by the
    /// player, which the auto-collect list skips too).</summary>
    public static ulong NoRoomMask(Ship ship, ulong mask)
    {
        ulong full = 0;
        ResourceManager rm = ship.Resources;
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0 && (ship.Metadata.IsResourceTypeBanned(_resources[i].ID) || rm.GetRemainingCapacity(_resources[i].ID, includeUntypedStorages: true) <= 0))
            {
                full |= 1UL << i;
            }
        }
        return full;
    }

    /// <summary>Junk body (asteroid / wreck) or other ship whose nearest part touches our hull, or null.</summary>
    public static Ship? TouchingBody(SimRoot sim, Ship ship)
    {
        Circle me = ship.WorldBoundingCircle;
        ShipBuffer.Clear();
        sim.Ships.DetFindIntersectingWith(new Circle(me.Center, me.Radius + 150f), ShipBuffer, approximate: true, junk: true);
        Ship? best = null;
        float bestD = float.MaxValue;
        for (int i = 0; i < ShipBuffer.Count; i++)
        {
            Ship s = ShipBuffer[i];
            if (s == ship || s.Parts.Count == 0)
            {
                continue;
            }
            Vector2? p = NearestPartCenter(s, me.Center);
            float d = p is Vector2 pv ? pv.DistanceTo(me.Center) - me.Radius : s.WorldBoundingCircle.DistanceTo(me);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        ShipBuffer.Clear();
        return bestD < 100f ? best : null;
    }

    /// <summary>
    /// Bounding circles (inflated by <paramref name="pad"/>) of every body other than us and <paramref name="skip"/> that
    /// crosses the segment a->b: the whole cluster in the way, as routing obstacles. Circles containing a or b are left
    /// out (the router skips them too, ShipAgent.PushObstacles).
    /// </summary>
    public static void CorridorBodies(SimRoot sim, Ship ship, Vector2 a, Vector2 b, float pad, Ship? skip, List<Circle> into)
    {
        into.Clear();
        ShipBuffer.Clear();
        sim.Ships.DetFindIntersectingWith(new Circle((a + b) * 0.5f, a.DistanceTo(b) * 0.5f + pad), ShipBuffer, approximate: true, junk: true);
        Vector2 d = b - a;
        float len2 = d.LengthSquared;
        for (int i = 0; i < ShipBuffer.Count; i++)
        {
            Ship s = ShipBuffer[i];
            if (s == ship || s == skip || s.Parts.Count == 0)
            {
                continue;
            }
            Circle c = s.WorldBoundingCircle;
            c = new Circle(c.Center, c.Radius + pad);
            float t = len2 > 0f ? Math.Clamp(((c.Center.X - a.X) * d.X + (c.Center.Y - a.Y) * d.Y) / len2, 0f, 1f) : 0f;
            if (!c.Contains(a) && !c.Contains(b) && (a + d * t).DistanceTo(c.Center) < c.Radius)
            {
                into.Add(c);
            }
        }
        ShipBuffer.Clear();
    }

    /// <summary>World center of the part of <paramref name="s"/> nearest to <paramref name="from"/>.</summary>
    public static Vector2? NearestPartCenter(Ship s, Vector2 from)
    {
        Vector2? best = null;
        float bestD2 = float.MaxValue;
        int count = s.Parts.Count;
        for (int p = 0; p < count; p++)
        {
            Vector2 c = s.Parts[p].WorldCenter;
            float d2 = c.DistanceSquaredTo(from);
            if (d2 < bestD2)
            {
                bestD2 = d2;
                best = c;
            }
        }
        return best;
    }

    /// <summary>Any ship in the sim with this ObjectID string (used once per reconcile, not per tick).</summary>
    public static Ship? FindShipById(SimRoot sim, string id)
    {
        var ships = sim.Ships;
        for (int i = 0; i < ships.Count; i++)
        {
            if (ships[i].UniqueID.ToString() == id)
            {
                return ships[i];
            }
        }
        return null;
    }
    /// <summary>Keep-on-board amount = vanilla per-ship resource desire (ShipMetadata.GetResourceDesire, set with
    /// ResourceLimitsWindow.MPSetResourceDesireInput; also shown/edited in the game's own resource-limits window).</summary>
    public static int Keep(Ship ship, ID<ResourceRules> id) => ship.Metadata.GetResourceDesire(id, MPValueType.Displayed);

    /// <summary>
    /// Cargo hold in cells, as the player sees it: cargo bays are FlexResourceGrids whose tiles each hold one stack of
    /// any type (FlexResourceGrid.ResourceTile: Resources / ResourceType). Typed grids (ammo racks, factory and
    /// reactor buffers) are not cargo and are ignored. Cells holding an offloadable type count as used; cells
    /// needed for a resource's keep-on-board amount (or for a "keep all" type) are reserved, so they are taken
    /// out of both numerator and denominator.
    /// </summary>
    public static void CargoCells(Ship ship, ulong offMask, out int used, out int total, out int reserved)
    {
        used = 0;
        total = 0;
        reserved = 0;
        EnsureGroups();
        Span<int> cellsByType = stackalloc int[64];
        foreach (var grid in Cosmoteer.Ships.Parts.Resources.FlexResourceGrid.GetAllOnShip(ship))
        {
            foreach (var tile in grid)
            {
                total++;
                if (tile.HasResources)
                {
                    int idx = IndexOf(tile.ResourceType!.Value);
                    if (idx >= 0)
                    {
                        cellsByType[idx]++;
                    }
                }
            }
        }
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            int cells = cellsByType[i];
            if (cells == 0)
            {
                continue;
            }
            if ((offMask & (1UL << i)) == 0)
            {
                reserved += cells; // "keep all": never offloaded
                continue;
            }
            int keepCells = Math.Min(cells, Cells(_resources[i], Keep(ship, _resources[i].ID)));
            reserved += keepCells;
            used += cells - keepCells;
        }
    }

    /// <summary>
    /// Parks a follow next to the base's cargo instead of at bounding-circle distance (BaseFollowCommand follows by
    /// center distance = EdgeToEdgeFollowRadius + both bounding radii, which leaves a large gap for elongated ships).
    /// Side = direction from the base center to its cargo-bay tile nearest the miner (FollowAngle is world-relative,
    /// measured from the target center); distance = base hull extent on that side + miner hull extent + small gap,
    /// both from part positions. The follow target is skipped by ship collision avoidance, so it can get this close.
    /// Returns the planned center distance.
    /// </summary>
    public static float ParkNear(Ship baseShip, Ship miner, Cosmoteer.Ships.Commands.BaseFollowCommand f)
    {
        // Work in the base's local frame: the cargo side and the hull extent along it don't change as the base turns,
        // and a ship-relative FollowAngle (FollowAngle + target rotation, BaseFollowCommand) keeps the miner on that
        // side while the base rotates.
        Vector2 lc = baseShip.LocalCenter;
        Vector2 mc = miner.WorldCenter;
        Vector2 anchorLocal = lc;
        float bestD2 = float.MaxValue;
        foreach (var grid in Cosmoteer.Ships.Parts.Resources.FlexResourceGrid.GetAllOnShip(baseShip))
        {
            foreach (var tile in grid)
            {
                float d2 = baseShip.TransformPointToWorld(tile.Center).DistanceSquaredTo(mc);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    anchorLocal = tile.Center;
                }
            }
        }
        Vector2 dirLocal = anchorLocal - lc;
        if (dirLocal.Length < 1f)
        {
            // No cargo bay (or it is central): use the side facing the miner, converted into the base's frame.
            Vector2 w0 = baseShip.TransformPointToWorld(lc);
            Vector2 wx = baseShip.TransformPointToWorld(lc + new Vector2(1f, 0f)) - w0;
            Vector2 wy = baseShip.TransformPointToWorld(lc + new Vector2(0f, 1f)) - w0;
            Vector2 toMiner = mc - w0;
            dirLocal = new Vector2(toMiner.X * wx.X + toMiner.Y * wx.Y, toMiner.X * wy.X + toMiner.Y * wy.Y);
        }
        float len = dirLocal.Length;
        Vector2 n = len > 0.001f ? dirLocal * (1f / len) : new Vector2(1f, 0f);
        float baseExt = LocalExtent(baseShip, lc, n) * baseShip.UniformScale;
        Vector2 worldDir = baseShip.TransformPointToWorld(lc + n) - baseShip.TransformPointToWorld(lc);
        float wl = worldDir.Length;
        worldDir = wl > 0.001f ? worldDir * (1f / wl) : new Vector2(1f, 0f);
        float minerExt = WorldExtent(miner, mc, -worldDir);
        float centerDist = baseExt + minerExt + 3f;
        f.FollowAngle = Vector2.Zero.DirectionTo(n);
        f.UseWorldRelativeFollowAngle = false;
        f.EdgeToEdgeFollowRadius = centerDist - miner.WorldBoundingRadius - baseShip.WorldBoundingRadius;
        return centerDist;
    }

    /// <summary>Hull extent in ship-local cells: corners of every part's rotated cell rect (Part.Rect) projected on n.</summary>
    private static float LocalExtent(Ship s, Vector2 c, Vector2 n)
    {
        float max = 0f;
        int count = s.Parts.Count;
        for (int i = 0; i < count; i++)
        {
            var r = s.Parts[i].Rect;
            max = Math.Max(max, Proj(r.X, r.Y, c, n));
            max = Math.Max(max, Proj(r.X + r.Width, r.Y, c, n));
            max = Math.Max(max, Proj(r.X, r.Y + r.Height, c, n));
            max = Math.Max(max, Proj(r.X + r.Width, r.Y + r.Height, c, n));
        }
        return max;
    }

    private static float Proj(float x, float y, Vector2 c, Vector2 n) => (x - c.X) * n.X + (y - c.Y) * n.Y;

    /// <summary>Hull extent in world units along world direction n: part rect corners transformed to world.</summary>
    private static float WorldExtent(Ship s, Vector2 c, Vector2 n)
    {
        float max = 0f;
        int count = s.Parts.Count;
        for (int i = 0; i < count; i++)
        {
            var r = s.Parts[i].Rect;
            max = Math.Max(max, WProj(s, r.X, r.Y, c, n));
            max = Math.Max(max, WProj(s, r.X + r.Width, r.Y, c, n));
            max = Math.Max(max, WProj(s, r.X, r.Y + r.Height, c, n));
            max = Math.Max(max, WProj(s, r.X + r.Width, r.Y + r.Height, c, n));
        }
        return max;
    }

    private static float WProj(Ship s, float x, float y, Vector2 c, Vector2 n)
    {
        Vector2 p = s.TransformPointToWorld(new Vector2(x, y)) - c;
        return p.X * n.X + p.Y * n.Y;
    }
    /// <summary>Units per storage cell = ResourceRules.MaxStackSize (one storage tile holds one stack).</summary>
    public static int Cells(ResourceRules r, int amount)
    {
        int per = r.MaxStackSize > 0 ? r.MaxStackSize : 1;
        return (amount + per - 1) / per;
    }

    /// <summary>
    /// Offloadable cargo above the keep amounts, and the space left for it (0..1 fill).
    /// Typed storage is summed per type; untyped (shared) cargo is counted once (max over types).
    /// </summary>
    public static float CargoFill(Ship ship, ulong mask, out int stored, out int remaining)
    {
        stored = 0;
        int typed = 0;
        int untyped = 0;
        ResourceManager rm = ship.Resources;
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0)
            {
                continue;
            }
            ID<ResourceRules> id = _resources[i].ID;
            stored += Math.Max(0, rm.GetResourceTotal(id) - Keep(ship, id));
            int typedOnly = rm.GetRemainingCapacity(id, includeUntypedStorages: false);
            int withUntyped = rm.GetRemainingCapacity(id, includeUntypedStorages: true);
            typed += typedOnly;
            untyped = Math.Max(untyped, withUntyped - typedOnly);
        }
        remaining = typed + untyped;
        int total = stored + remaining;
        if (total <= 0)
        {
            return 0f;
        }
        return (float)stored / total;
    }
    /// <summary>
    /// Queues crew transfers of the masked resource types from <paramref name="from"/> to <paramref name="to"/>,
    /// mirroring the vanilla Crew &amp; Resource Transfer window (CrewAndResourceTransferWindow.DoTransfer):
    /// SearchAllAvailableResourceSources/Sinks + MPMoveResourcesInput through NetManager (lockstep-safe).
    /// Returns (queued amount, whether any sink with capacity existed).
    /// </summary>
    /// <param name="fromCargo">Donor cargo policy: its effective keep-min / keep-all are never given away.</param>
    /// <param name="toCargo">With <paramref name="fillToTarget"/>: receiver policy whose keep-min is its target.</param>
    /// <param name="fillToTarget">Supply: send only the receiver's deficit (Logistics.Need), not all it can store.</param>
    /// <param name="sell">Trade: a sale to a station (TradeType.Sell at its current refund, like CrewAndResourceTransferWindow.DoTransfer
    /// :765-773); the donor budget is the Sell policy (Logistics.Sellable), types without a positive refund are skipped.</param>
    public static int QueueTransfer(GameRoot game, Ship from, Ship to, ulong mask, out bool anySink, Autofleet.CargoPolicy? fromCargo,
        Autofleet.CargoPolicy? toCargo = null, bool fillToTarget = false, bool sell = false)
    {
        anySink = false;
        int queued = 0;
        Ship owner = to.Commands.IsCommandableBy(game.LocalPlayerIndex) ? to : from;
        Autofleet.CargoPolicy? recvCargo = toCargo ?? Autofleet.Logistics.CargoOf(to);
        for (int i = 0; i < _resources.Length && i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0)
            {
                continue;
            }
            ID<ResourceRules> type = _resources[i].ID;
            Sources.Clear();
            Sinks.Clear();
            Scheduled.Clear();
            // Anticipated pickups/deliveries live on the job owner (the base, MPMoveResourcesInput.GetJobOwner),
            // so search from its perspective or every re-queue would double-book the same cargo.
            from.Resources.SearchAllAvailableResourceSources(to, type, MPValueType.Displayed, Sources, owner);
            if (Sources.Count == 0)
            {
                continue;
            }
            to.Resources.SearchAllAvailableResourceSinks(from, type, MPValueType.Displayed, Sinks, owner);
            if (Sinks.Count == 0)
            {
                continue;
            }
            anySink = true;
            // Never go below the floor (keep-all, or max of desire and effective keep-min), with every open pickup from
            // the donor counted (Logistics.Free); Supply also stops at the receiver's deficit.
            int price = sell ? game.Sim.Mode.GetResourceSellRefund(type, to.Metadata.PlayerIndex) : 0;
            if (sell && price <= 0)
            {
                continue;
            }
            int budget = sell ? Autofleet.Logistics.Sellable(from, type, fromCargo) : Autofleet.Logistics.Free(from, type, fromCargo);
            if (fillToTarget)
            {
                budget = Math.Min(budget, Autofleet.Logistics.Need(to, type, toCargo));
            }
            // Receiver keep-max ("не больше") is an intake ceiling for every automatic transfer.
            budget = Math.Min(budget, Autofleet.Logistics.Headroom(to, type, recvCargo));
            if (budget <= 0)
            {
                continue;
            }
            // Same ordering as the vanilla transfer window (_CompareSources / _CompareSinks).
            Sources.Sort(static (a, b) =>
            {
                int c = a.Source.SortPriority.CompareTo(b.Source.SortPriority);
                return c != 0 ? c : a.Distance.CompareTo(b.Distance);
            });
            Sinks.Sort(static (a, b) =>
            {
                int c = b.Sink.GetSortPriority(externalDelivery: false, MPValueType.Displayed)
                    .CompareTo(a.Sink.GetSortPriority(externalDelivery: false, MPValueType.Displayed));
                return c != 0 ? c : a.Distance.CompareTo(b.Distance);
            });
            for (int s = 0; s < Sources.Count; s++)
            {
                var (source, available, _) = Sources[s];
                while (available > 0 && budget > 0)
                {
                    IResourceSink? sink = null;
                    int cap = 0;
                    for (int k = 0; k < Sinks.Count; k++)
                    {
                        var (candidate, anticipated, _) = Sinks[k];
                        Scheduled.TryGetValue(candidate, out int already);
                        int left = anticipated - already;
                        if (left > 0 && source.IsSinkReachable(candidate, allowDifferentShips: true, allowAirlockTraversal: true))
                        {
                            sink = candidate;
                            cap = left;
                            break;
                        }
                    }
                    if (sink == null)
                    {
                        break;
                    }
                    int amount = Math.Min(Math.Min(available, cap), budget);
                    var manualId = MPMoveResourcesInput.GetJobOwner(source, sink, game.LocalPlayerIndex).Jobs.AllocLocalJobID();
                    game.NetManager.EnqueueInput(sell
                        ? new MPMoveResourcesInput(source, sink, amount, manualId, sticky: false, TradeType.Sell, price)
                        : new MPMoveResourcesInput(source, sink, amount, manualId));
                    Scheduled.TryGetValue(sink, out int prev);
                    Scheduled[sink] = prev + amount;
                    available -= amount;
                    budget -= amount;
                    queued += amount;
                }
            }
        }
        return queued;
    }

    /// <summary>True while a manual transfer job moving cargo out of <paramref name="miner"/> is still open on its owner.</summary>
    public static bool HasPendingOffload(GameRoot game, Ship miner, Ship baseShip)
    {
        Ship owner = baseShip.Commands.IsCommandableBy(game.LocalPlayerIndex) ? baseShip : miner;
        var jobs = owner.Resources.ManualTransferJobs;
        for (int i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (j.Source != null && j.Source.Ship == miner && j.Sink.Ship == baseShip)
            {
                return true;
            }
        }
        return false;
    }

    private static ulong _seenNuggetMask;

    /// <summary>Bitmask of resource types seen as loose nuggets so far.</summary>
    public static ulong SeenNuggetMask => _seenNuggetMask;

    private static readonly List<Nugget> NuggetBuffer = new();

    /// <summary>Fills NuggetBuffer from the per-type nugget spatial index (SimNuggetManager.DetFindIntersectingWith).</summary>
    private static void QueryNuggets(SimRoot sim, in Circle region, ulong wantMask)
    {
        NuggetBuffer.Clear();
        SimNuggetManager nuggets = sim.Nuggets;
        for (int t = 0; t < _resources.Length && t < 64; t++)
        {
            ResourceRules r = _resources[t];
            if (!r.IsStackable || (wantMask & (1UL << t)) == 0)
            {
                continue;
            }
            int before = NuggetBuffer.Count;
            nuggets.DetFindIntersectingWith(r.ID, region, NuggetBuffer);
            if (NuggetBuffer.Count > before)
            {
                _seenNuggetMask |= 1UL << t;
            }
        }
    }

    private static bool NuggetAllowed(SimRoot sim, Nugget n, bool areaMode, in Circle area)
    {
        Vector2 loc = n.WorldLocation;
        return n.IsWithinSightOfLocalPlayer && (!areaMode || area.Contains(loc)) && !IsHazardous(sim, new Circle(loc, 1f));
    }

    /// <summary>
    /// Nearest loose resource nugget the local player can see, of a wanted type, not already fully claimed by this
    /// miner, outside sun hazards. Area mode: inside the circle; Nearest mode: radius doubles from 4 km to maxDist.
    /// </summary>
    public static Nugget? FindNugget(SimRoot sim, Ship miner, Vector2 origin, bool areaMode, in Circle area, float maxDist, ulong wantMask,
        Dictionary<Nugget, long> blacklist, byte[]? ranks, out double bestCost)
    {
        float r = Math.Min(FirstSearchRadius, maxDist);
        while (true)
        {
            Circle region = areaMode ? area : new Circle(origin, r);
            QueryNuggets(sim, region, wantMask);
            Nugget? best = null;
            bestCost = double.MaxValue;
            for (int i = 0; i < NuggetBuffer.Count; i++)
            {
                Nugget n = NuggetBuffer[i];
                float d2 = n.WorldLocation.DistanceSquaredTo(origin);
                Diag.NuggetsSeen++;
                if (blacklist.ContainsKey(n) || n.Quantity - miner.Resources.GetAnticipatedPickUp(n, MPValueType.Displayed) <= 0)
                {
                    Diag.Excluded++;
                    continue;
                }
                if (!NuggetAllowed(sim, n, areaMode, area))
                {
                    Diag.Sun++; // not visible / outside area / sun
                    continue;
                }
                if (ThreatZones.Count > 0 && InThreat(n.WorldLocation))
                {
                    Diag.Threat++;
                    continue;
                }
                int t = IndexOf(n.ResourceType);
                double c = CostOf(d2, t >= 0 ? 1UL << t : 0, ranks);
                if (c >= bestCost)
                {
                    continue;
                }
                best = n;
                bestCost = c;
            }
            NuggetBuffer.Clear();
            if ((best != null && (ranks == null || RingDone(bestCost, r))) || areaMode || r >= maxDist)
            {
                return best;
            }
            r = Math.Min(r * 2f, maxDist);
        }
    }

    /// <summary>
    /// Queues crew pickups for wanted nuggets within crew reach that also pass the area/visibility/sun checks,
    /// exactly like the vanilla nugget click (PlayerInputHandler._TryCollectNugget): GetBestReceivingSink +
    /// MPMoveResourcesInput. Returns amount queued.
    /// </summary>
    public static int CollectNuggetsNear(GameRoot game, Ship miner, float reach, ulong wantMask, bool areaMode, in Circle area, Autofleet.CargoPolicy? cargo)
    {
        SimRoot sim = game.Sim;
        Circle shipCircle = miner.WorldBoundingCircle;
        QueryNuggets(sim, new Circle(shipCircle.Center, shipCircle.Radius + reach), wantMask);
        int queued = 0;
        for (int i = 0; i < NuggetBuffer.Count; i++)
        {
            Nugget n = NuggetBuffer[i];
            if (shipCircle.DistanceTo(n.WorldLocation) >= reach || !NuggetAllowed(sim, n, areaMode, area))
            {
                continue;
            }
            int left = n.Quantity - miner.Resources.GetAnticipatedPickUp(n, MPValueType.Displayed);
            // Keep-max: never request more than the headroom (each queued job is an anticipated delivery from now on).
            left = Math.Min(left, Autofleet.Logistics.Headroom(miner, n.ResourceType, cargo));
            while (left > 0)
            {
                IResourceSink? sink = miner.Resources.GetBestReceivingSink(n, left, MPValueType.Displayed, out int cap);
                if (sink == null || cap <= 0)
                {
                    break;
                }
                int amount = Math.Min(left, cap);
                var jobId = MPMoveResourcesInput.GetJobOwner(n, sink, game.LocalPlayerIndex).Jobs.AllocLocalJobID();
                game.NetManager.EnqueueInput(new MPMoveResourcesInput(n, sink, amount, jobId));
                left -= amount;
                queued += amount;
            }
        }
        NuggetBuffer.Clear();
        return queued;
    }
    /// <summary>Own or allied (Mode.AreAllies) ship with the given ObjectID string; allies are valid offload sinks in vanilla.</summary>
    /// <summary>Whether this client drives <paramref name="s"/>: the synced owner (Metadata.PlayerIndex) is the local player.</summary>
    public static bool Drives(GameRoot game, Ship s) => Drives(s.Metadata.PlayerIndex, game.LocalPlayerIndex);

    public static Ship? FindFriendlyShip(GameRoot game, string id, Ship? except)
    {
        SimRoot sim = game.Sim;
        var ships = sim.Ships;
        for (int i = 0; i < ships.Count; i++)
        {
            Ship s = ships[i];
            if (s == except || s.IsJunk)
            {
                continue;
            }
            if ((s.IsLocalPlayerShip || sim.Mode.AreAllies(game.LocalPlayerIndex, s.Metadata.PlayerIndex)) && s.UniqueID.ToString() == id)
            {
                return s;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- threat zones / routing (Avoid reaction)
    // Vanilla pathing only avoids ships physically (CollisionAvoidanceFlags.Ships), doodads and nebulas
    // (CollisionAvoidanceFlags, Command.cs:1110-1140); there is no weapon-range avoidance, so routing around enemies
    // is done here with plain MoveCommand waypoints.

    /// <summary>Threat circles of the current search (empty unless the miner uses the Avoid reaction).</summary>
    public static readonly List<Circle> ThreatZones = new();
    /// <summary>Velocity (units/s) of each enemy zone, same index as ThreatZones (routing obstacles pushed later have none).</summary>
    public static readonly List<Vector2> ThreatVels = new();
    public static void ClearThreats() { ThreatZones.Clear(); ThreatVels.Clear(); }
    public static float MinerSpeed(Ship s) => s.Physics.Body is { } b ? ((Vector2)b.LinearVelocity).Length : 0f;
    // Weak keys: every enemy ever checked would otherwise stay alive for the whole process.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ship, System.Runtime.CompilerServices.StrongBox<(long At, float Range)>> RangeCache = new();
    private const float ThreatMargin = 400f;
    private const float UnknownThreatRange = 1500f;

    /// <summary>
    /// Fills ThreatZones with one circle per enemy on radar: max standard weapon range (Weapon.GetStandardRange over
    /// WeaponManager.OperationalWeapons) + its bounding radius + margin. Contacts on radar but not in sight ("?") get a
    /// smaller fixed zone, since the player can't see their weapons.
    /// </summary>
    public static void BuildThreatZones(SimRoot sim, Ship miner, long nowMs)
    {
        ThreatZones.Clear();
        ThreatVels.Clear();
        ShipBuffer.Clear();
        float scan = Math.Max(miner.Sensors.RadarRadius, 8000f);
        sim.Ships.DetFindIntersectingWith(new Circle(miner.WorldCenter, scan), ShipBuffer, approximate: true, junk: false);
        for (int i = 0; i < ShipBuffer.Count; i++)
        {
            Ship s = ShipBuffer[i];
            if (s == miner || s.Rules.IsAsteroid || !s.IsSignificant || !miner.IsEnemiesWith(s) || !s.IsWithinRadarOfLocalPlayer)
            {
                continue;
            }
            bool seen = s.IsWithinSightOfLocalPlayer;
            float range = seen ? WeaponRange(s, nowMs) : UnknownThreatRange;
            ThreatZones.Add(new Circle(s.WorldCenter, range + s.WorldBoundingRadius + ThreatMargin));
            // Unseen contacts ("?"): don't read hidden motion; assume it closes on us at our speed (min 100/s).
            var body = s.Physics.Body;
            Vector2 toUs = miner.WorldCenter - s.WorldCenter;
            float l = toUs.Length;
            ThreatVels.Add(seen && body != null ? (Vector2)body.LinearVelocity
                : l > 1f ? toUs * (Math.Max(MinerSpeed(miner), 100f) / l) : default);
        }
        ShipBuffer.Clear();
    }

    /// <summary>Max standard weapon range of <paramref name="s"/> (5 s cache, capped 8000); unarmed/unknown = 1500.</summary>
    internal static float WeaponRange(Ship s, long nowMs)
    {
        if (RangeCache.TryGetValue(s, out var c) && nowMs - c.Value.At < 5000)
        {
            return c.Value.Range;
        }
        float max = 0f;
        var weapons = s.Weapons.OperationalWeapons;
        for (int i = 0; i < weapons.Count; i++)
        {
            float r = weapons[i].GetStandardRange();
            if (!float.IsInfinity(r) && !float.IsNaN(r))
            {
                max = Math.Max(max, r);
            }
        }
        max = Math.Min(max, 8000f);
        if (max <= 0f)
        {
            max = UnknownThreatRange; // unarmed or unknown: keep a modest berth
        }
        RangeCache.AddOrUpdate(s, new((nowMs, max)));
        return max;
    }

    public static bool InThreat(in Vector2 p, float pad = 0f)
    {
        for (int i = 0; i < ThreatZones.Count; i++)
        {
            if (p.DistanceTo(ThreatZones[i].Center) < ThreatZones[i].Radius + pad)
            {
                return true;
            }
        }
        return false;
    }

    public static bool FirstBlockingPublic(Vector2 a, Vector2 b) => FirstBlocking(a, b) >= 0;

    /// <summary>Route look-ahead: an enemy zone also covers where its current velocity carries it (Escape.Horizon).</summary>
    private const int SweepSamples = 5;

    /// <summary>Center of sample <paramref name="k"/> of zone <paramref name="i"/>'s swept path (stuck obstacles have no velocity).</summary>
    private static Vector2 SweptCenter(int i, int k) =>
        i < ThreatVels.Count ? ThreatZones[i].Center + ThreatVels[i] * (MiningDrone.Autofleet.Escape.Horizon * k / (SweepSamples - 1)) : ThreatZones[i].Center;

    /// <summary>Inside any zone now or along its predicted path. shortcut: sampled capsule (5 circles, gaps above
    /// ~2R/4 per Horizon of speed), upgrade to an exact segment-capsule test if fast small zones slip through.</summary>
    private static bool InSwept(Vector2 p)
    {
        for (int i = 0; i < ThreatZones.Count; i++)
        {
            for (int k = 0; k < SweepSamples; k++)
            {
                if (p.DistanceTo(SweptCenter(i, k)) < ThreatZones[i].Radius)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>First threat zone (now or along its predicted path) the segment a->b passes through, nearest to a, or -1.</summary>
    private static int FirstBlocking(Vector2 a, Vector2 b)
    {
        int best = -1;
        float bestT = float.MaxValue;
        Vector2 d = b - a;
        float len2 = d.LengthSquared;
        for (int i = 0; i < ThreatZones.Count; i++)
        {
            for (int k = 0; k < SweepSamples; k++)
            {
                Vector2 c = SweptCenter(i, k);
                float t = len2 > 0f ? Math.Clamp(((c.X - a.X) * d.X + (c.Y - a.Y) * d.Y) / len2, 0f, 1f) : 0f;
                Vector2 closest = a + d * t;
                if (closest.DistanceTo(c) < ThreatZones[i].Radius && t < bestT)
                {
                    bestT = t;
                    best = i;
                }
            }
        }
        return best;
    }


    /// <summary>
    /// Waypoints from a to b that go around threat zones swept along their predicted path (velocity x Escape.Horizon):
    /// for the first blocking sample, try both sides
    /// (perpendicular offsets of radius+margin), keep the side whose point is clear and whose total length is shorter,
    /// then recurse on both legs (depth-limited, so at most a handful of nodes). Returns false if no clear route found.
    /// </summary>
    public static bool Route(Vector2 a, Vector2 b, List<Vector2> waypoints, int depth = 0)
    {
        int k = FirstBlocking(a, b);
        if (k < 0)
        {
            return true;
        }
        if (depth >= 4)
        {
            return false;
        }
        Vector2 dir = b - a;
        float len = dir.Length;
        Vector2 n = len > 1f ? new Vector2(-dir.Y / len, dir.X / len) : new Vector2(0f, 1f);
        float off = ThreatZones[k].Radius + ThreatMargin;
        // Pass the swept zone on either side of its outermost samples across the path (a static zone: its center).
        Vector2 hi = SweptCenter(k, 0), lo = hi;
        for (int s = 1; s < SweepSamples; s++)
        {
            Vector2 c = SweptCenter(k, s);
            float dc = c.X * n.X + c.Y * n.Y;
            if (dc > hi.X * n.X + hi.Y * n.Y)
            {
                hi = c;
            }
            if (dc < lo.X * n.X + lo.Y * n.Y)
            {
                lo = c;
            }
        }
        Vector2 p1 = hi + n * off;
        Vector2 p2 = lo - n * off;
        bool ok1 = !InSwept(p1), ok2 = !InSwept(p2);
        float l1 = ok1 ? a.DistanceTo(p1) + p1.DistanceTo(b) : float.MaxValue;
        float l2 = ok2 ? a.DistanceTo(p2) + p2.DistanceTo(b) : float.MaxValue;
        if (!ok1 && !ok2)
        {
            return false;
        }
        // Build the detour on BOTH sides (each fully recursed) and keep the shorter complete route. A failed or longer
        // preferred side no longer hides a valid other side. Only runs when a travel command is issued.
        List<Vector2>? best = null;
        float bestLen = float.MaxValue;
        for (int side = 0; side < 2; side++)
        {
            if (side == 0 ? !ok1 : !ok2)
            {
                continue;
            }
            Vector2 p = side == 0 ? p1 : p2;
            var cand = new List<Vector2>();
            if (!Route(a, p, cand, depth + 1))
            {
                continue;
            }
            cand.Add(p);
            if (!Route(p, b, cand, depth + 1))
            {
                continue;
            }
            float total = 0f;
            Vector2 prev = a;
            foreach (Vector2 q in cand)
            {
                total += prev.DistanceTo(q);
                prev = q;
            }
            total += prev.DistanceTo(b);
            if (total < bestLen)
            {
                bestLen = total;
                best = cand;
            }
        }
        if (best == null)
        {
            return false;
        }
        waypoints.AddRange(best);
        return true;
    }

    /// <summary>Point at <paramref name="dist"/> from <paramref name="center"/> on its least-threatened side (16 directions).</summary>
    public static Vector2 SafeSideOf(Vector2 center, float dist)
    {
        Vector2 best = center;
        float bestScore = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double ang = i * Math.PI / 8.0;
            Vector2 p = center + new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang)) * dist;
            float score = float.MaxValue;
            for (int z = 0; z < ThreatZones.Count; z++)
            {
                score = Math.Min(score, p.DistanceTo(ThreatZones[z].Center) - ThreatZones[z].Radius);
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        return best;
    }

    /// <summary>Escape heading: of 16 directions, the one whose point 4 km out is furthest from every threat edge.</summary>
    public static Vector2 EscapePoint(Vector2 from)
    {
        Vector2 best = from;
        float bestScore = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double ang = i * Math.PI / 8.0;
            Vector2 p = from + new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang)) * 4000f;
            float score = float.MaxValue;
            for (int z = 0; z < ThreatZones.Count; z++)
            {
                score = Math.Min(score, p.DistanceTo(ThreatZones[z].Center) - ThreatZones[z].Radius);
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        return best;
    }

    /// <summary>Nearest enemy in sight whose hull gap to <paramref name="ship"/> is within either side's max weapon range
    /// (it can reach us, or we can reach it), or null. Filters before picking, so a nearer unreachable contact never hides it.</summary>
    public static Ship? FindEnemyInRange(SimRoot sim, Ship ship, long nowMs)
    {
        ShipBuffer.Clear();
        sim.Ships.DetFindIntersectingWith(ship.Sensors.DetWorldRadarCircle, ShipBuffer, approximate: true, junk: false);
        Ship? best = null;
        float bestD2 = float.MaxValue;
        Vector2 c = ship.WorldCenter;
        float own = WeaponRange(ship, nowMs);
        for (int i = 0; i < ShipBuffer.Count; i++)
        {
            Ship s = ShipBuffer[i];
            if (s == ship || s.Rules.IsAsteroid || s.IsJunk || !s.IsSignificant || !ship.IsEnemiesWith(s) || !s.IsWithinSightOfLocalPlayer)
            {
                continue;
            }
            float d2 = s.WorldCenter.DistanceSquaredTo(c);
            if (d2 >= bestD2 || MathF.Sqrt(d2) - s.WorldBoundingRadius - ship.WorldBoundingRadius > Math.Max(own, WeaponRange(s, nowMs)))
            {
                continue;
            }
            best = s;
            bestD2 = d2;
        }
        ShipBuffer.Clear();
        return best;
    }

    /// <summary>Nearest enemy on our radar (not junk / asteroid) whose center lies inside <paramref name="area"/>, or null (Hunt).</summary>
    public static Ship? FindEnemyIn(SimRoot sim, Ship ship, Circle area)
    {
        ShipBuffer.Clear();
        sim.Ships.DetFindIntersectingWith(area, ShipBuffer, approximate: true, junk: false);
        Ship? best = null;
        float bestD2 = float.MaxValue;
        Vector2 c = ship.WorldCenter;
        float r2 = area.Radius * area.Radius;
        for (int i = 0; i < ShipBuffer.Count; i++)
        {
            Ship s = ShipBuffer[i];
            if (s == ship || s.Rules.IsAsteroid || s.IsJunk || !s.IsSignificant || !ship.IsEnemiesWith(s) || !s.IsWithinRadarOfLocalPlayer
                || s.WorldCenter.DistanceSquaredTo(area.Center) > r2)
            {
                continue;
            }
            float d2 = s.WorldCenter.DistanceSquaredTo(c);
            if (d2 < bestD2)
            {
                best = s;
                bestD2 = d2;
            }
        }
        ShipBuffer.Clear();
        return best;
    }

    /// <summary>Nearest visible enemy (not junk / asteroid) inside the miner's radar circle, or null.</summary>
    public static Ship? FindEnemy(SimRoot sim, Ship miner)
    {
        ShipBuffer.Clear();
        sim.Ships.DetFindIntersectingWith(miner.Sensors.DetWorldRadarCircle, ShipBuffer, approximate: true, junk: false);
        Ship? best = null;
        float bestD2 = float.MaxValue;
        Vector2 c = miner.WorldCenter;
        for (int i = 0; i < ShipBuffer.Count; i++)
        {
            Ship s = ShipBuffer[i];
            if (s == miner || s.Rules.IsAsteroid || !s.IsSignificant || !miner.IsEnemiesWith(s) || !s.IsWithinRadarOfLocalPlayer)
            {
                continue;
            }
            float d2 = s.WorldCenter.DistanceSquaredTo(c);
            if (d2 < bestD2)
            {
                best = s;
                bestD2 = d2;
            }
        }
        ShipBuffer.Clear();
        return best;
    }
}
