using System.Collections.Generic;

namespace MiningDrone.Autofleet;

/// <summary>Effective state of one module type for a ship.</summary>
internal struct Slot
{
    public bool Present;

    /// <summary>Enabled after overrides, activation, implementation, conflicts and dependencies.</summary>
    public bool Active;
    public int Priority;

    /// <summary>Stable order of the instance in the profile (tie-break).</summary>
    public int Index;
    public string? InstanceId;
}

/// <summary>
/// Typed, flattened view of an assignment (defaults -> live profile -> ship overrides), compiled only when the
/// assignment, any other assignment or the profile library changes, so the 3 Hz tick reads plain fields and
/// allocates nothing.
/// </summary>
internal sealed class AgentConfig
{
    public int CompiledFrom = int.MinValue;
    public int CompiledEdit = int.MinValue;
    public int CompiledLib = int.MinValue;

    /// <summary>Bumped by every recompile (caches derived from the config key on it).</summary>
    public int Compiles;
    public ShipAssignment? Source;

    public Slot Mine, Loot, Offload, Anchor, Avoid, Retreat, Guard, Defend, Hunt, Supply, Crew, Trade, Production;
    public bool HuntAnchor;
    public float HuntRange;

    public HarvestScope MineScope, LootScope;
    public float MineMaxSearch, LootMaxSearch;
    public bool LootWrecks, LootLoose;

    /// <summary>Junk ship sources of Loot (World.KindWreck | KindCache | KindDerelict).</summary>
    public int LootKinds;

    /// <summary>Loot has any source on.</summary>
    public bool LootAny => LootKinds != 0 || LootLoose;
    public float Threshold;
    public bool ParkIdle;
    public float WorkRadius;
    public bool FollowIdle;
    public bool Formation;
    public int AvoidAttempts;
    public long RetreatResumeMs, GuardResumeMs;
    public int RetreatTrigger;
    public float RetreatHull;
    public bool RetreatRepair;
    public int CrewTarget, CrewKeep;
    public bool CrewRedistribute, CrewHire;
    public int CrewCredits;
    public bool TradeTravel;
    public long TradeCooldownMs;
    public bool DefendEvade;
    public long DefendResumeMs;
    public bool ResumeAfterManual;
    public long MinCommitMs;

    public string? AnchorId;
    public bool AnchorCycle;
    public AnchorLink AnchorLink = new();
    public string? OffloadId;
    public float ZoneX, ZoneY, ZoneR;
    public TempIntent Temp;

    public List<string>? CollectPriority;
    public List<string>? KeepAll;

    /// <summary>Validation messages (conflicts, missing dependencies, unimplemented, cycles) shown as blockers.</summary>
    public readonly List<string> Blockers = new();

    public static float EffectiveSearch(float v) => v >= Registry.UnlimitedSearch ? 1.0e7f : v;

    public bool Anchored => Anchor.Active && AnchorId != null && !AnchorCycle;
}

internal static class Resolver
{
    /// <summary>Recompiles <paramref name="c"/> when the assignment changed. Returns true if it did.</summary>
    public static bool Refresh(AgentConfig c, ShipAssignment a)
    {
        // Any edit anywhere bumps EditVersion: anchor cycles depend on other ships' bindings. Any profile edit bumps
        // LibraryVersion: profiles are read live, so the next tick after an edit runs with it.
        if (ReferenceEquals(c.Source, a) && c.CompiledFrom == a.Version && c.CompiledEdit == Store.EditVersion && c.CompiledLib == Store.LibraryVersion)
        {
            return false;
        }
        c.Source = a;
        c.CompiledFrom = a.Version;
        c.CompiledEdit = Store.EditVersion;
        c.CompiledLib = Store.LibraryVersion;
        c.Compiles++;
        Compile(c, a);
        return true;
    }

    public static double Param(ShipAssignment a, ModuleInstance m, string key) => ParamOf(m, key);

    /// <summary>One-time split: store each missing inherited param with its current value, so a later edit of the
    /// source param (e.g. wrecks) no longer drags it along. Called by the profile editor before any param edit.</summary>
    public static void Materialize(ModuleInstance m)
    {
        if (Registry.Get(m.Type) is not { } d)
        {
            return;
        }
        foreach (ParamDef q in d.Params)
        {
            if (q.Inherit != null && !m.Params.ContainsKey(q.Key))
            {
                m.Params[q.Key] = ParamOf(m, q.Key);
            }
        }
    }

    /// <summary>Stored value, else the inherited param's (ParamDef.Inherit), else the default.</summary>
    public static double ParamOf(ModuleInstance m, string key)
    {
        if (m.Params.TryGetValue(key, out double v))
        {
            return v;
        }
        ParamDef? pd = Registry.Get(m.Type)?.Param(key);
        return pd?.Inherit is string from && from != key ? ParamOf(m, from) : pd?.Default ?? 0;
    }

    public static bool Enabled(ShipAssignment a, ModuleInstance m) => m.Enabled;

    /// <summary>Effective param of the first instance of a type (UI helper); null when the type is absent.</summary>
    public static double? Param(ShipAssignment a, string typeId, string key)
    {
        ModuleInstance? m = a.Profile.First(typeId);
        return m == null ? null : Param(a, m, key);
    }

    public static bool? Enabled(ShipAssignment a, string typeId)
    {
        ModuleInstance? m = a.Profile.First(typeId);
        return m == null ? null : Enabled(a, m);
    }

    private static void Compile(AgentConfig c, ShipAssignment a)
    {
        c.Blockers.Clear();
        c.Mine = c.Loot = c.Offload = c.Anchor = c.Avoid = c.Retreat = c.Guard = c.Defend = c.Hunt = c.Supply = c.Crew = c.Trade = c.Production = default;
        Profile s = a.Profile;
        if (ReferenceEquals(s, Store.Missing))
        {
            c.Blockers.Add(L.T("MiningDrone/BlockNoProfile"));
        }
        // Target and formation offset are the ship's; every anchor policy value (follow, leash, missing, hysteresis) is the profile's.
        c.AnchorLink = s.Defaults.Anchor.Clone();
        c.AnchorLink.TargetShipId = a.Bindings.Anchor.TargetShipId;
        c.AnchorLink.OffsetX = a.Bindings.Anchor.OffsetX;
        c.AnchorLink.OffsetY = a.Bindings.Anchor.OffsetY;
        c.AnchorId = string.IsNullOrEmpty(a.Bindings.Anchor.TargetShipId) ? null : a.Bindings.Anchor.TargetShipId;
        c.AnchorCycle = c.AnchorId != null && Anchors.InCycle(a.ShipId);
        if (c.AnchorCycle)
        {
            c.Blockers.Add(L.T("MiningDrone/BlockCycle"));
        }
        bool anchorBound = c.AnchorId != null && !c.AnchorCycle;

        // Pass 1: enabled + activation + implementation; first instance per type wins.
        var seen = new HashSet<string>();
        for (int i = 0; i < s.Modules.Count; i++)
        {
            ModuleInstance m = s.Modules[i];
            ModuleDef? d = Registry.Get(m.Type);
            if (d == null)
            {
                c.Blockers.Add(L.F("MiningDrone/BlockUnknown", m.Type));
                continue;
            }
            if (!seen.Add(m.Type))
            {
                c.Blockers.Add(L.F("MiningDrone/BlockDuplicate", L.T(d.NameKey)));
                continue;
            }
            bool on = Enabled(a, m);
            if (on && m.DefVersion > d.SchemaVersion)
            {
                // Written by a newer build: its params may mean something else here. Never run it silently.
                c.Blockers.Add(L.F("MiningDrone/BlockNewerVersion", L.T(d.NameKey), m.DefVersion));
                on = false;
            }
            if (on && !d.Implemented)
            {
                c.Blockers.Add(L.F("MiningDrone/BlockNotImplemented", L.T(d.NameKey)));
                on = false;
            }
            if (on && m.Activation == Activation.WhenAnchored && !anchorBound)
            {
                on = false;
            }
            if (on && m.Activation == Activation.WhenNotAnchored && anchorBound)
            {
                on = false;
            }
            var slot = new Slot { Present = true, Active = on, Priority = m.Priority, Index = i, InstanceId = m.Id };
            switch (d.Type)
            {
                case ModuleType.Mine: c.Mine = slot; break;
                case ModuleType.Loot: c.Loot = slot; break;
                case ModuleType.Offload: c.Offload = slot; break;
                case ModuleType.AnchorLink: c.Anchor = slot; break;
                case ModuleType.Avoidance: c.Avoid = slot; break;
                case ModuleType.Retreat: c.Retreat = slot; break;
                case ModuleType.Guard: c.Guard = slot; break;
                case ModuleType.SelfDefence: c.Defend = slot; break;
                case ModuleType.Hunt: c.Hunt = slot; break;
                case ModuleType.Supply: c.Supply = slot; break;
                case ModuleType.Crew: c.Crew = slot; break;
                case ModuleType.Trade: c.Trade = slot; break;
                case ModuleType.Production: c.Production = slot; break;
            }
        }

        // Pass 2: conflicts (the lower-priority side is disabled; tie -> later instance) and dependencies.
        for (int i = 0; i < s.Modules.Count; i++)
        {
            ModuleInstance m = s.Modules[i];
            ModuleDef? d = Registry.Get(m.Type);
            if (d == null || !IsActive(c, d.Type, m.Id))
            {
                continue;
            }
            foreach (string other in d.Conflicts)
            {
                ModuleDef? od = Registry.Get(other);
                if (od == null)
                {
                    continue;
                }
                ref Slot os = ref SlotOf(c, od.Type, out bool has);
                if (!has || !os.Active)
                {
                    continue;
                }
                ref Slot me = ref SlotOf(c, d.Type, out _);
                bool iLose = me.Priority < os.Priority || (me.Priority == os.Priority && me.Index > os.Index);
                if (iLose)
                {
                    me.Active = false;
                    c.Blockers.Add(L.F("MiningDrone/BlockConflict", L.T(d.NameKey), L.T(od.NameKey)));
                }
                else
                {
                    os.Active = false;
                    c.Blockers.Add(L.F("MiningDrone/BlockConflict", L.T(od.NameKey), L.T(d.NameKey)));
                }
            }
            foreach (string req in d.Requires)
            {
                ModuleDef? rd = Registry.Get(req);
                if (rd == null)
                {
                    continue;
                }
                ref Slot rs = ref SlotOf(c, rd.Type, out bool has);
                if (!has || !rs.Active)
                {
                    ref Slot me = ref SlotOf(c, d.Type, out _);
                    me.Active = false;
                    c.Blockers.Add(L.F("MiningDrone/BlockRequires", L.T(d.NameKey), L.T(rd.NameKey)));
                }
            }
        }

        double P(in Slot slot, string type, string key)
        {
            ModuleInstance? m = slot.Present ? s.First(type) : null;
            return m != null ? Param(a, m, key) : Registry.Get(type)!.Param(key)!.Default;
        }

        c.MineScope = (HarvestScope)(int)P(c.Mine, Registry.Mine, Registry.PScope);
        c.MineMaxSearch = (float)P(c.Mine, Registry.Mine, Registry.PMaxSearch);
        c.LootScope = (HarvestScope)(int)P(c.Loot, Registry.Loot, Registry.PScope);
        c.LootMaxSearch = (float)P(c.Loot, Registry.Loot, Registry.PMaxSearch);
        c.LootWrecks = P(c.Loot, Registry.Loot, Registry.PWrecks) != 0;
        c.LootLoose = P(c.Loot, Registry.Loot, Registry.PLoose) != 0;
        c.LootKinds = (c.LootWrecks ? World.KindWreck : 0) | (P(c.Loot, Registry.Loot, Registry.PCaches) != 0 ? World.KindCache : 0)
            | (P(c.Loot, Registry.Loot, Registry.PDerelicts) != 0 ? World.KindDerelict : 0);
        c.Threshold = (float)P(c.Offload, Registry.Offload, Registry.PThreshold);
        c.ParkIdle = P(c.Offload, Registry.Offload, Registry.PParkIdle) != 0;
        c.WorkRadius = (float)P(c.Anchor, Registry.AnchorLink, Registry.PWorkRadius);
        c.FollowIdle = P(c.Anchor, Registry.AnchorLink, Registry.PFollowIdle) != 0;
        c.Formation = P(c.Anchor, Registry.AnchorLink, Registry.PFormation) != 0;
        c.AvoidAttempts = (int)P(c.Avoid, Registry.Avoidance, Registry.PAttempts);
        c.RetreatResumeMs = (long)(P(c.Retreat, Registry.Retreat, Registry.PResumeDelay) * 1000);
        c.RetreatTrigger = (int)P(c.Retreat, Registry.Retreat, Registry.PTrigger);
        c.GuardResumeMs = (long)(P(c.Guard, Registry.Guard, Registry.PResumeDelay) * 1000);
        c.DefendEvade = P(c.Defend, Registry.SelfDefence, Registry.PResponse) == 1;
        c.DefendResumeMs = (long)(P(c.Defend, Registry.SelfDefence, Registry.PResumeDelay) * 1000);
        c.HuntAnchor = P(c.Hunt, Registry.Hunt, Registry.PScope) == 1;
        c.HuntRange = (float)P(c.Hunt, Registry.Hunt, Registry.PRange);
        c.RetreatHull = (float)P(c.Retreat, Registry.Retreat, Registry.PHull);
        c.RetreatRepair = P(c.Retreat, Registry.Retreat, Registry.PRepair) != 0;
        c.CrewTarget = (int)P(c.Crew, Registry.Crew, Registry.PCrewTarget);
        c.CrewKeep = (int)P(c.Crew, Registry.Crew, Registry.PKeep);
        c.CrewRedistribute = P(c.Crew, Registry.Crew, Registry.PRedistribute) != 0;
        c.CrewHire = P(c.Crew, Registry.Crew, Registry.PHire) != 0;
        c.CrewCredits = (int)P(c.Crew, Registry.Crew, Registry.PCredits);
        c.TradeTravel = P(c.Trade, Registry.Trade, Registry.PTravel) != 0;
        c.TradeCooldownMs = (long)(P(c.Trade, Registry.Trade, Registry.PCooldown) * 1000);
        if ((c.Mine.Active && c.MineScope == HarvestScope.Anchor) || (c.Loot.Active && c.LootScope == HarvestScope.Anchor) || (c.Hunt.Active && c.HuntAnchor))
        {
            if (!c.Anchor.Active)
            {
                c.Blockers.Add(L.T("MiningDrone/BlockScopeNeedsAnchor"));
            }
        }
        c.ResumeAfterManual = s.Arbitration.ResumeAfterManual;
        c.MinCommitMs = s.Arbitration.MinCommitMs;
        c.ZoneX = a.Bindings.Zone.X;
        c.ZoneY = a.Bindings.Zone.Y;
        c.ZoneR = a.Bindings.Zone.R;
        string? off = string.IsNullOrEmpty(a.Bindings.OffloadTarget) ? null : a.Bindings.OffloadTarget;
        c.OffloadId = off ?? (s.Defaults.OffloadToAnchor && anchorBound ? c.AnchorId : null);
        c.Temp = a.Temp;
        CargoPolicy cargo = a.Cargo;
        c.CollectPriority = cargo.CollectPriority;
        c.KeepAll = cargo.KeepAll;
        Logistics.Problems(cargo, c.Blockers);
    }

    private static bool IsActive(AgentConfig c, ModuleType t, string id)
    {
        ref Slot s = ref SlotOf(c, t, out bool has);
        return has && s.Active && s.InstanceId == id;
    }

    private static Slot _none;

    private static ref Slot SlotOf(AgentConfig c, ModuleType t, out bool has)
    {
        has = true;
        switch (t)
        {
            case ModuleType.Mine: return ref c.Mine;
            case ModuleType.Loot: return ref c.Loot;
            case ModuleType.Offload: return ref c.Offload;
            case ModuleType.AnchorLink: return ref c.Anchor;
            case ModuleType.Avoidance: return ref c.Avoid;
            case ModuleType.Retreat: return ref c.Retreat;
            case ModuleType.Guard: return ref c.Guard;
            case ModuleType.SelfDefence: return ref c.Defend;
            case ModuleType.Hunt: return ref c.Hunt;
            case ModuleType.Supply: return ref c.Supply;
            case ModuleType.Crew: return ref c.Crew;
            case ModuleType.Trade: return ref c.Trade;
            case ModuleType.Production: return ref c.Production;
        }
        has = false;
        _none = default;
        return ref _none;
    }
}

/// <summary>Anchor graph over assignments: cyclic follow dependencies are rejected.</summary>
internal static class Anchors
{
    /// <summary>True when following anchors from <paramref name="shipId"/> returns to it.</summary>
    public static bool InCycle(string shipId)
    {
        string? cur = shipId;
        for (int i = 0; i < 64 && cur != null; i++)
        {
            ShipAssignment? a = Store.Find(cur);
            if (a == null || !a.Enabled)
            {
                return false;
            }
            cur = a.Bindings.Anchor.TargetShipId;
            if (cur == shipId)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Would anchoring <paramref name="shipId"/> to <paramref name="targetId"/> create a cycle?</summary>
    public static bool WouldCycle(string shipId, string targetId)
    {
        if (shipId == targetId)
        {
            return true;
        }
        string? cur = targetId;
        for (int i = 0; i < 64 && cur != null; i++)
        {
            if (cur == shipId)
            {
                return true;
            }
            ShipAssignment? a = Store.Find(cur);
            cur = a?.Bindings.Anchor.TargetShipId;
        }
        return false;
    }
}
