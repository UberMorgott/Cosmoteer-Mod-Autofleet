using System;
using System.Collections.Generic;

namespace MiningDrone.Autofleet;

/// <summary>Module catalog (spec §3) and the starter profiles. Starter profiles are data, not code paths.</summary>
internal static class Registry
{
    public const string AnchorLink = "anchor";
    public const string Guard = "guard";
    public const string Hunt = "hunt";
    public const string Mine = "mine";
    public const string Loot = "loot";
    public const string Offload = "offload";
    public const string Support = "support";
    public const string Build = "build";
    public const string Trade = "trade";
    public const string Crew = "crew";
    public const string Retreat = "retreat";
    public const string Avoidance = "avoid";
    public const string Production = "production";
    public const string SelfDefence = "defend";
    public const string Supply = "supply";

    // Param keys
    public const string PScope = "scope";
    public const string PMaxSearch = "maxSearch";
    public const string PWrecks = "wrecks";
    public const string PLoose = "loose";
    public const string PCaches = "caches";
    public const string PDerelicts = "derelicts";
    public const string PThreshold = "threshold";
    public const string PParkIdle = "parkIdle";
    public const string PWorkRadius = "workRadius";
    public const string PFollowIdle = "followIdle";
    public const string PAttempts = "attempts";
    public const string PResumeDelay = "resumeDelay";
    public const string PTrigger = "trigger";
    public const string PRange = "range";
    public const string PResponse = "response";
    public const string PFormation = "formation";
    public const string PHull = "hull";
    public const string PRepair = "repair";
    public const string PCrewTarget = "crewTarget";
    public const string PKeep = "keep";
    public const string PRedistribute = "redistribute";
    public const string PHire = "hire";
    public const string PCredits = "credits";
    public const string PTravel = "travel";
    public const string PCooldown = "cooldown";

    /// <summary>Slider value at/above which the Nearest search is unlimited (whole sector).</summary>
    public const float UnlimitedSearch = 80000f;

    private static readonly string[] ScopeChoices = { "MiningDrone/ModeArea", "MiningDrone/ModeNearest", "MiningDrone/ScopeAnchor" };
    private static readonly string[] RetreatTriggers = { "MiningDrone/TrigEnemy", "MiningDrone/TrigDamage" };
    private static readonly string[] HuntScopes = { "MiningDrone/ModeArea", "MiningDrone/ScopeAnchor" }; // stable indexes
    private static readonly string[] DefendResponses = { "MiningDrone/RespFire", "MiningDrone/RespEvade" };

    public static readonly ModuleDef[] All =
    {
        new()
        {
            TypeId = AnchorLink, Type = ModuleType.AnchorLink, Channel = Channel.Movement, Implemented = true, DefaultPriority = 40,
            Params = new[]
            {
                new ParamDef(PWorkRadius, ParamKind.Number, 3000, 250, 20000, 50),
                new ParamDef(PFollowIdle, ParamKind.Bool, 1),
                new ParamDef(PFormation, ParamKind.Bool, 0), // off for profiles saved before it existed
            },
        },
        new()
        {
            TypeId = Guard, Type = ModuleType.Guard, Channel = Channel.Movement, Implemented = true, DefaultPriority = 80,
            Params = new[]
            {
                new ParamDef(PResumeDelay, ParamKind.Number, 5, 1, 60, 1),
            },
            Conflicts = new[] { Avoidance },
        },
        new()
        {
            // Seeks out enemies inside its area (work zone, or range around the anchor) and attacks them; leashed at
            // 1.5x the area radius, then flies back before hunting again.
            TypeId = Hunt, Type = ModuleType.Hunt, Channel = Channel.Movement, Implemented = true, DefaultPriority = 75, Conflicts = new[] { Avoidance },
            Params = new[]
            {
                new ParamDef(PScope, ParamKind.Choice, 0, choices: HuntScopes),
                new ParamDef(PRange, ParamKind.Number, 6000, 1000, 30000, 250),
            },
        },
        new()
        {
            TypeId = Mine, Type = ModuleType.Mine, Channel = Channel.Movement, Implemented = true, DefaultPriority = 50,
            Params = new[]
            {
                new ParamDef(PScope, ParamKind.Choice, 0, choices: ScopeChoices),
                new ParamDef(PMaxSearch, ParamKind.Number, 30000, 2000, UnlimitedSearch, 500),
            },
        },
        new()
        {
            TypeId = Loot, Type = ModuleType.Loot, Channel = Channel.Movement, Implemented = true, DefaultPriority = 50,
            Params = new[]
            {
                new ParamDef(PScope, ParamKind.Choice, 0, choices: ScopeChoices),
                new ParamDef(PMaxSearch, ParamKind.Number, 30000, 2000, UnlimitedSearch, 500),
                new ParamDef(PWrecks, ParamKind.Bool, 0),
                new ParamDef(PLoose, ParamKind.Bool, 0),
                // Split out of "wrecks": a profile saved before they existed keeps what its wrecks toggle covered.
                new ParamDef(PCaches, ParamKind.Bool, 0) { Inherit = PWrecks },
                new ParamDef(PDerelicts, ParamKind.Bool, 0) { Inherit = PWrecks },
            },
        },
        new()
        {
            TypeId = Offload, Type = ModuleType.Offload, Channel = Channel.Movement, Implemented = true, DefaultPriority = 60,
            Params = new[]
            {
                new ParamDef(PThreshold, ParamKind.Number, 0.9, 0.1, 1, 0.01),
                new ParamDef(PParkIdle, ParamKind.Bool, 1),
            },
        },
        new() { TypeId = Support, Type = ModuleType.Support, Channel = Channel.Movement, DefaultPriority = 55 },
        new() { TypeId = Build, Type = ModuleType.Build, Channel = Channel.Movement, DefaultPriority = 52 },
        new()
        {
            // Sell-only (D18): surplus allowed by the cargo Sell policy goes to a discovered station of this sector (travel
            // on), or only to one already in transfer range (travel off: warehouse). Refusing/full stations cool down.
            TypeId = Trade, Type = ModuleType.Trade, Channel = Channel.Movement, Implemented = true, DefaultPriority = 51, Requires = new[] { Avoidance },
            Params = new[]
            {
                new ParamDef(PTravel, ParamKind.Bool, 1),
                new ParamDef(PCooldown, ParamKind.Number, 120, 10, 1800, 10), // s before a refusing/full station is tried again
            },
        },
        new()
        {
            // Background: crewTarget = crew this ship wants (0 = receives none); with redistribute it gives crew above
            // `keep` (aboard) to its anchor while the anchor's own Crew target is short; with hire it tops itself up to crewTarget
            // from any crew provider in range, spending only credits above `credits`.
            TypeId = Crew, Type = ModuleType.Crew, Channel = Channel.Background, Implemented = true, DefaultPriority = 30,
            Params = new[]
            {
                new ParamDef(PCrewTarget, ParamKind.Number, 0, 0, 500, 1),
                new ParamDef(PKeep, ParamKind.Number, 10, 0, 500, 1),
                new ParamDef(PRedistribute, ParamKind.Bool, 0),
                new ParamDef(PHire, ParamKind.Bool, 0), // hire up to crewTarget from a crew provider in range
                new ParamDef(PCredits, ParamKind.Number, 5000, 0, 1000000, 500), // credits left after hiring (no debt)
            },
        },
        new()
        {
            TypeId = Retreat, Type = ModuleType.Retreat, Channel = Channel.Policy, Emergency = true, Implemented = true, DefaultPriority = 100,
            Params = new[]
            {
                new ParamDef(PTrigger, ParamKind.Choice, 0, choices: RetreatTriggers),
                new ParamDef(PResumeDelay, ParamKind.Number, 5, 1, 60, 1),
                new ParamDef(PHull, ParamKind.Number, 0.65, 0.1, 0.95, 0.05), // damage trigger: retreat below this value integrity
                new ParamDef(PRepair, ParamKind.Bool, 0), // opt-in: vanilla "repair all" when safe
            },
        },
        new()
        {
            TypeId = Avoidance, Type = ModuleType.Avoidance, Channel = Channel.Policy, Emergency = true, Implemented = true, DefaultPriority = 95,
            Params = new[]
            {
                new ParamDef(PAttempts, ParamKind.Number, 3, 1, 10, 1),
            },
        },
        // Background (D19): factories whose output has a keep-max on this ship stop at it and restart below a watermark.
        new() { TypeId = Production, Type = ModuleType.Production, Channel = Channel.Background, Implemented = true, DefaultPriority = 20 },
        new()
        {
            // Armed ships without Guard: an enemy within weapons range (its or ours) is shot at without taking movement,
            // or escaped from on the predictive arc. Idle while Guard is active (Guard fights).
            TypeId = SelfDefence, Type = ModuleType.SelfDefence, Channel = Channel.Policy, Emergency = true, Implemented = true, DefaultPriority = 90,
            Params = new[]
            {
                new ParamDef(PResponse, ParamKind.Choice, 0, choices: DefendResponses),
                new ParamDef(PResumeDelay, ParamKind.Number, 5, 1, 60, 1),
            },
        },
        new()
        {
            // Background: tops the anchor up to its keep-on-board amounts from our surplus above our own keep, while the
            // anchor is within crew transfer range (Anchor-link does the moving). Receiver = anchor only for now.
            TypeId = Supply, Type = ModuleType.Supply, Channel = Channel.Background, Implemented = true, DefaultPriority = 35,
            Requires = new[] { AnchorLink },
        },
    };

    private static readonly Dictionary<string, ModuleDef> ById = Build_();

    private static Dictionary<string, ModuleDef> Build_()
    {
        var d = new Dictionary<string, ModuleDef>();
        foreach (ModuleDef m in All)
        {
            d[m.TypeId] = m;
        }
        return d;
    }

    public static ModuleDef? Get(string typeId) => ById.TryGetValue(typeId, out var d) ? d : null;

    public static ModuleInstance NewInstance(string typeId, string id, bool enabled = true)
    {
        ModuleDef d = ById[typeId];
        var m = new ModuleInstance { Id = id, Type = typeId, DefVersion = d.SchemaVersion, Enabled = enabled, Priority = d.DefaultPriority };
        foreach (ParamDef p in d.Params)
        {
            m.Params[p.Key] = p.Default;
        }
        return m;
    }

    public static string NewId() => System.Guid.NewGuid().ToString("N").Substring(0, 12);

    /// <summary>
    /// Drag-drop of a module in the composer, whose order is priority desc then list index: take the priority of the
    /// next module (<paramref name="before"/>) and sit right before it in the list, or at the bottom take the previous
    /// one's (<paramref name="after"/>) and sit right after it. The display then matches the drop and no other priority
    /// changes. False when nothing changed.
    /// </summary>
    public static bool MoveModule(Profile p, ModuleInstance m, ModuleInstance? before, ModuleInstance? after)
    {
        ModuleInstance? n = before ?? after;
        if (n == null || ReferenceEquals(n, m))
        {
            return false;
        }
        bool moved = MiningDrone.SortMath.MoveNear(p.Modules, m, before, after);
        if (!moved && m.Priority == n.Priority)
        {
            return false;
        }
        m.Priority = n.Priority;
        return true;
    }

    /// <summary>Shown value of a number param (slider caption and collapsed-module summary).</summary>
    public static string NumberText(ParamDef pd, double v, string infinite) =>
        pd.Key == PThreshold || pd.Key == PHull ? (v * 100).ToString("0") + "%"
        : pd.Key == PMaxSearch && v >= UnlimitedSearch ? infinite
        : v.ToString(pd.Step >= 1 ? "0" : "0.##");

    /// <summary>One line of a folded module's effective values: "label: value · ...", a bool as its label when on.
    /// Reads via Resolver.ParamOf (never Materialize), so the instance is not modified.</summary>
    public static string Summary(ModuleInstance m, ModuleDef d, System.Func<string, string> t)
    {
        var parts = new List<string>();
        foreach (ParamDef pd in d.Params)
        {
            double v = Resolver.ParamOf(m, pd.Key);
            if (pd.Kind == ParamKind.Bool)
            {
                if (v != 0)
                {
                    parts.Add(t(pd.LabelKey));
                }
                continue;
            }
            string shown = pd.Kind == ParamKind.Choice ? t(pd.Choices![System.Math.Clamp((int)v, 0, pd.Choices.Length - 1)]) : NumberText(pd, v, t("MiningDrone/Infinite"));
            parts.Add(t(pd.LabelKey) + ": " + shown);
        }
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------------------ starter profiles
    public const string StarterMiner = "miner";
    public static readonly string[] StarterKeys = { StarterMiner, "fighter", "cow", "builder", "trader", "guard" };

    /// <summary>Stable profile/instance IDs for starters, so a migrated assignment and a fresh library agree.</summary>
    public static Profile CreateStarter(string key)
    {
        var p = new Profile { Id = "starter-" + key, StarterKey = key, Name = "" };
        ModuleInstance M(string type, bool on)
        {
            ModuleInstance m = NewInstance(type, key + "-" + type, on);
            if (type == AnchorLink)
            {
                m.Params[PFormation] = 1; // new starters hold distinct slots around a shared anchor
            }
            return m;
        }
        switch (key)
        {
            case StarterMiner:
                p.Modules.Add(M(Avoidance, true));
                p.Modules.Add(M(Retreat, false));
                p.Modules.Add(M(Guard, false));
                p.Modules.Add(M(Offload, true));
                p.Modules.Add(M(Mine, true));
                p.Modules.Add(M(SelfDefence, true)); // return fire (new libraries only: existing profiles are never edited)
                p.Modules.Add(M(Loot, true));
                p.Modules.Add(M(AnchorLink, true));
                break;
            case "fighter":
                p.Modules.Add(M(Guard, true));
                var h = M(Hunt, false);
                h.Params[PScope] = 1; // around the anchor
                p.Modules.Add(h);
                var r = M(Retreat, true);
                r.Params[PTrigger] = 1; // retreat on damage, not on every radar contact
                p.Modules.Add(r);
                p.Modules.Add(M(AnchorLink, true));
                p.Defaults.UseAnchor = true;
                break;
            case "cow":
                p.Modules.Add(M(Avoidance, true));
                p.Modules.Add(M(Support, false));
                p.Modules.Add(M(Supply, false));
                p.Modules.Add(M(Offload, false));
                p.Modules.Add(M(Crew, false));
                p.Modules.Add(M(SelfDefence, true)); // return fire (new libraries only: existing profiles are never edited)
                p.Modules.Add(M(AnchorLink, true));
                p.Defaults.UseAnchor = true;
                break;
            case "builder":
                p.Modules.Add(M(Avoidance, true));
                p.Modules.Add(M(Build, false));
                p.Modules.Add(M(SelfDefence, true)); // return fire (new libraries only: existing profiles are never edited)
                p.Modules.Add(M(AnchorLink, true));
                break;
            case "trader":
                p.Modules.Add(M(Avoidance, true));
                p.Modules.Add(M(Retreat, false));
                p.Modules.Add(M(Trade, false));
                p.Modules.Add(M(SelfDefence, true)); // return fire (new libraries only: existing profiles are never edited)
                p.Modules.Add(M(Offload, false));
                break;
            case "guard":
                p.Modules.Add(M(Guard, true));
                p.Modules.Add(M(AnchorLink, true));
                p.Defaults.UseAnchor = true;
                break;
        }
        return p;
    }

    /// <summary>Display name: the user's name, or the localized starter name while a starter is unrenamed.</summary>
    public static string DisplayName(string name, string? profileId)
    {
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }
        if (profileId != null && profileId.StartsWith("starter-", System.StringComparison.Ordinal))
        {
            return L.T(string.Concat("MiningDrone/Starter_", profileId.AsSpan(8)));
        }
        return "?";
    }
}
