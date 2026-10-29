using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiningDrone.Autofleet;

/// <summary>
/// Forward-compatible persistence: JSON fields this build does not know (written by a newer version) are kept in
/// <see cref="Extra"/> and written back unchanged, and copied by every Clone. New fields can therefore be
/// added later without a breaking schema change. Choice params are stored as stable indexes (never renumbered).
/// </summary>
internal abstract class Extensible
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    protected static Dictionary<string, JsonElement>? CopyExtra(Dictionary<string, JsonElement>? e) =>
        e == null ? null : new Dictionary<string, JsonElement>(e);
}

/// <summary>Runtime handler kinds. Data refers to modules by the stable string <see cref="ModuleDef.TypeId"/>.</summary>
internal enum ModuleType
{
    AnchorLink,
    Guard,
    Hunt,
    Mine,
    Loot,
    Offload,
    Support,
    Build,
    Trade,
    Crew,
    Retreat,
    Avoidance,
    Production,
    SelfDefence,
    Supply,
}

/// <summary>Intent channel: movement modules compete for the single movement owner; policies add constraints or
/// emergency intents; background modules may run next to the movement owner (none implemented yet).</summary>
internal enum Channel
{
    Movement,
    Policy,
    Background,
}

internal enum ParamKind
{
    Bool,
    Number,
    Choice,
}

/// <summary>When an enabled module instance may take part in arbitration.</summary>
internal enum Activation
{
    Always = 0,
    WhenAnchored = 1,
    WhenNotAnchored = 2,
}

/// <summary>Assignment-level temporary intent (not a profile or module): survives interrupts and save/load.</summary>
internal enum TempIntent
{
    None = 0,
    ReturnHome = 1,
    UnloadNow = 2,
}

/// <summary>What a module does when its anchor can't be resolved (lost, out of sim or in stasis). The binding is
/// never cleared or replaced automatically.</summary>
internal enum MissingAnchorPolicy
{
    Wait = 0,
    Rally = 1,
    WorkWithout = 2,
}

/// <summary>Harvest search scope (Mine / Loot).</summary>
internal enum HarvestScope
{
    Zone = 0,
    Nearest = 1,
    Anchor = 2,
}

internal sealed class ParamDef
{
    public ParamDef(string key, ParamKind kind, double def, double min = 0, double max = 1, double step = 0, string[]? choices = null)
    {
        Key = key;
        Kind = kind;
        Default = def;
        Min = min;
        Max = max;
        Step = step;
        Choices = choices;
    }

    public string Key { get; }
    public ParamKind Kind { get; }
    public double Default { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    /// <summary>String keys (MiningDrone/...) of the choice labels, index = value.</summary>
    public string[]? Choices { get; }

    /// <summary>A module saved without this param takes that param's value instead of <see cref="Default"/>.</summary>
    public string? Inherit { get; init; }

    public string LabelKey => "MiningDrone/P_" + Key;

    public double Clamp(double v)
    {
        if (Kind == ParamKind.Bool)
        {
            return v != 0 ? 1 : 0;
        }
        if (Kind == ParamKind.Choice)
        {
            int n = Choices?.Length ?? 1;
            int i = (int)System.Math.Round(v);
            return i < 0 ? 0 : (i >= n ? n - 1 : i);
        }
        return v < Min ? Min : (v > Max ? Max : v);
    }
}

/// <summary>A registered capability type: schema version, parameter descriptors, channel, dependencies, conflicts.</summary>
internal sealed class ModuleDef
{
    public string TypeId { get; init; } = "";
    public ModuleType Type { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public Channel Channel { get; init; }

    /// <summary>Policy that can raise an emergency intent (beats every task intent).</summary>
    public bool Emergency { get; init; }

    /// <summary>False = listed in the catalog and storable in profiles, but never activates yet.</summary>
    public bool Implemented { get; init; }
    public int DefaultPriority { get; init; }
    public ParamDef[] Params { get; init; } = System.Array.Empty<ParamDef>();
    public string[] Requires { get; init; } = System.Array.Empty<string>();
    public string[] Conflicts { get; init; } = System.Array.Empty<string>();
    public string NameKey => "MiningDrone/M_" + TypeId;
    public string HintKey => "MiningDrone/MH_" + TypeId;

    public ParamDef? Param(string key)
    {
        for (int i = 0; i < Params.Length; i++)
        {
            if (Params[i].Key == key)
            {
                return Params[i];
            }
        }
        return null;
    }
}

/// <summary>Configuration of one module inside a profile. Runtime state is kept by the ship agent, not here.</summary>
internal sealed class ModuleInstance : Extensible
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int DefVersion { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public Activation Activation { get; set; }
    public Dictionary<string, double> Params { get; set; } = new();

    public ModuleInstance Clone() => new()
    {
        Id = Id,
        Type = Type,
        DefVersion = DefVersion,
        Enabled = Enabled,
        Priority = Priority,
        Activation = Activation,
        Params = new Dictionary<string, double>(Params),
        Extra = CopyExtra(Extra),
    };
}

internal sealed class ArbitrationSettings : Extensible
{
    /// <summary>After a manual order: true = take back control once the player's commands are done; false = hard pause.</summary>
    public bool ResumeAfterManual { get; set; } = true;

    /// <summary>A higher-priority task module may preempt the owner only after it has owned movement this long.</summary>
    public int MinCommitMs { get; set; } = 1000;

    public ArbitrationSettings Clone() => new() { ResumeAfterManual = ResumeAfterManual, MinCommitMs = MinCommitMs, Extra = CopyExtra(Extra) };
}

/// <summary>
/// Cargo policy of a profile (or a per-ship override that replaces it whole). Resource IDs are ResourceRules ID
/// strings. Keep-min is the vanilla per-ship resource desire; it is pushed to the ship (MPSetResourceDesireInput)
/// whenever the ship's effective value changes (MinerManager.SyncKeep). KeepMax / Sell / Pickup: economy (P4, D17), see Logistics.
/// </summary>
internal sealed class CargoPolicy : Extensible
{
    /// <summary>Collect template (not the effective filter: that is each ship's vanilla ban set). Toggles are pushed to the
    /// profile's ships as bans, an explicit assignment copies it once. null = everything; empty = nothing.</summary>
    public List<string>? Collect { get; set; }

    /// <summary>Collect priority, high to low (D18). null/empty = nearest target (no priority); selected types not listed
    /// share the last rank. Unknown IDs and unselected entries are kept as stored.</summary>
    public List<string>? CollectPriority { get; set; }

    /// <summary>Types never offloaded ("Всё").</summary>
    public List<string>? KeepAll { get; set; }

    /// <summary>Units kept aboard per type (absent = not managed by the profile).</summary>
    public Dictionary<string, int>? KeepMin { get; set; }

    /// <summary>"Не больше": automatic intake ceiling per type (absent = unmanaged; below keep-min = ignored + blocker).
    /// Mining/looting and incoming Offload/Supply stop at it; it is not a donor floor.</summary>
    public Dictionary<string, int>? KeepMax { get; set; }

    /// <summary>Sell permission per type: Logistics.Never (0, absent) / AboveMax (1) / AboveMin (2); unknown = off + blocker.</summary>
    public Dictionary<string, int>? Sell { get; set; }

    /// <summary>Pickup permission per type, same codes as Sell (stored; no hauler consumes it yet).</summary>
    public Dictionary<string, int>? Pickup { get; set; }

    public CargoPolicy Clone() => new()
    {
        Collect = Collect == null ? null : new List<string>(Collect),
        CollectPriority = CollectPriority == null ? null : new List<string>(CollectPriority),
        KeepAll = KeepAll == null ? null : new List<string>(KeepAll),
        KeepMin = KeepMin == null ? null : new Dictionary<string, int>(KeepMin),
        KeepMax = KeepMax == null ? null : new Dictionary<string, int>(KeepMax),
        Sell = Sell == null ? null : new Dictionary<string, int>(Sell),
        Pickup = Pickup == null ? null : new Dictionary<string, int>(Pickup),
        Extra = CopyExtra(Extra),
    };
}

/// <summary>Link to any ship (no privileged flagship). Gives no command authority over the target.</summary>
internal sealed class AnchorLink : Extensible
{
    public string? TargetShipId { get; set; }

    /// <summary>Edge-to-edge follow distance; negative = vanilla default.</summary>
    public float FollowDistance { get; set; } = -1f;

    /// <summary>Formation offset relative to the anchor (stored now, used by formation-capable Anchor-link in P2).</summary>
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }

    /// <summary>Max distance from the anchor while working; 0 = off.</summary>
    public float Leash { get; set; }
    public MissingAnchorPolicy MissingPolicy { get; set; }

    /// <summary>Moving-target hysteresis: an anchor-relative target is dropped only past work radius * this.</summary>
    public float Hysteresis { get; set; } = 1.5f;

    public AnchorLink Clone() => new()
    {
        TargetShipId = TargetShipId,
        FollowDistance = FollowDistance,
        OffsetX = OffsetX,
        OffsetY = OffsetY,
        Leash = Leash,
        MissingPolicy = MissingPolicy,
        Hysteresis = Hysteresis,
        Extra = CopyExtra(Extra),
    };
}

internal sealed class WorkZone : Extensible
{
    public float X { get; set; }
    public float Y { get; set; }
    public float R { get; set; } = 3000f;
}

/// <summary>Separate bindings: changing the anchor never silently redirects transfers.</summary>
internal sealed class TargetBindings : Extensible
{
    public AnchorLink Anchor { get; set; } = new();
    public string? OffloadTarget { get; set; }
    public string? SupplyTarget { get; set; }
    public string? ProtectedTarget { get; set; }
    public WorkZone Zone { get; set; } = new();
}

/// <summary>Profile defaults for bindings ("follow or not", anchor parameters). Ship IDs are per save, so a profile
/// never stores a concrete target.</summary>
internal sealed class BindingDefaults : Extensible
{
    public bool UseAnchor { get; set; }
    public bool OffloadToAnchor { get; set; } = true;
    public AnchorLink Anchor { get; set; } = new();

    public BindingDefaults Clone() => new() { UseAnchor = UseAnchor, OffloadToAnchor = OffloadToAnchor, Anchor = Anchor.Clone(), Extra = CopyExtra(Extra) };
}

/// <summary>Reserved (P5): finite build order template carried by a profile.</summary>
internal sealed class BuildOrderTemplate : Extensible
{
    public string Id { get; set; } = "";
    public string? BlueprintId { get; set; }
    public string? ProfileId { get; set; }
    public int Quantity { get; set; }
    public int Priority { get; set; }

    public BuildOrderTemplate Clone() => new() { Id = Id, BlueprintId = BlueprintId, ProfileId = ProfileId, Quantity = Quantity, Priority = Priority, Extra = CopyExtra(Extra) };
}

/// <summary>Behaviour template. Ships reference it by <see cref="Id"/> and read it live: every edit (rename included)
/// reaches all its ships on their next tick.</summary>
internal sealed class Profile : Extensible
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? StarterKey { get; set; }
    public List<ModuleInstance> Modules { get; set; } = new();
    public ArbitrationSettings Arbitration { get; set; } = new();
    public CargoPolicy Cargo { get; set; } = new();
    public BindingDefaults Defaults { get; set; } = new();
    public List<BuildOrderTemplate>? BuildOrders { get; set; }

    public ModuleInstance? First(string typeId)
    {
        for (int i = 0; i < Modules.Count; i++)
        {
            if (Modules[i].Type == typeId)
            {
                return Modules[i];
            }
        }
        return null;
    }
}

/// <summary>Persisted provenance for exact-save reconciliation (written only into per-save snapshots).</summary>
internal sealed class RuntimeRecord : Extensible
{
    public string? Module { get; set; }
    public int State { get; set; }
    public string? TargetShipId { get; set; }

    /// <summary>Own command IDs: [hasPlayer, playerId, sequence].</summary>
    public List<long[]>? OwnCommands { get; set; }

    /// <summary>Own self-defence weapon target IDs, same encoding (the game saves Weapon.ExplicitTargets).</summary>
    public List<long[]>? DefTargets { get; set; }

    /// <summary>Damage-retreat latch (between the trigger and the recovery threshold the hull alone can't tell).</summary>
    public bool DamageLatch { get; set; }
    public float ApproachX { get; set; }
    public float ApproachY { get; set; }

    /// <summary>Loot stasis approach: its junk source kind (World.JunkKind; 0 = none / saved before it existed).</summary>
    public int ApproachKind { get; set; }

    /// <summary>Station of a sale in progress (Trade), so a reload resumes it instead of leaving mid-transfer.</summary>
    public string? StationId { get; set; }
}

internal sealed class ShipAssignment : Extensible
{
    public string ShipId { get; set; } = "";
    public int OwnerIndex { get; set; } = -1;

    /// <summary>Automation on (false = draft: settings exist but the ship is not driven).</summary>
    public bool Enabled { get; set; }

    /// <summary>Explicit user pause (survives save/load).</summary>
    public bool UserPaused { get; set; }

    /// <summary>Stable id of the profile this ship runs (read live; see <see cref="Profile"/>).</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>Load-only: the pinned profile copy written by older builds (JSON "Snapshot"); migrated by Store.Normalize.</summary>
    [JsonPropertyName("Snapshot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LegacySnapshot? Legacy { get; set; }

    /// <summary>Fleet command only (targets, zone, formation offset). Every logic setting comes from the profile:
    /// there are no per-ship overrides (old ones are stripped on load by Store.Normalize).</summary>
    public TargetBindings Bindings { get; set; } = new();
    public string? Wing { get; set; }
    public TempIntent Temp { get; set; }
    public RuntimeRecord? Runtime { get; set; }

    /// <summary>Automation off with no pending pause or temporary order (the caller persists it).</summary>
    public void Disable()
    {
        Enabled = false;
        UserPaused = false;
        Temp = TempIntent.None;
    }

    /// <summary>Bumped on every edit; the agent recompiles its config when it changes.</summary>
    [JsonIgnore]
    public int Version { get; set; }

    /// <summary>The live profile, or <see cref="Store.Missing"/> (no modules, blocker) when the id is unknown.</summary>
    [JsonIgnore]
    public Profile Profile => Store.ProfileFor(ProfileId);

    [JsonIgnore]
    public CargoPolicy Cargo => Profile.Cargo;
}

/// <summary>The parts of an old pinned snapshot the migration needs; everything else in it is dropped.</summary>
internal sealed class LegacySnapshot
{
    public string? ProfileId { get; set; }
}

internal sealed class FleetStateFile : Extensible
{
    public int Schema { get; set; } = Store.SupportedSchema;
    public Dictionary<string, ShipAssignment> Assignments { get; set; } = new();
}

internal sealed class SaveSnapshotFile : Extensible
{
    public int Schema { get; set; } = Store.SupportedSchema;
    public string SavePath { get; set; } = "";
    public long SaveWriteTicksUtc { get; set; }
    public FleetStateFile State { get; set; } = new();
}

internal sealed class ProfileLibraryFile : Extensible
{
    public int Schema { get; set; } = Store.SupportedSchema;
    public List<string> SeededStarters { get; set; } = new();
    public List<Profile> Profiles { get; set; } = new();
}

internal sealed class PrefsFile : Extensible
{
    public bool ShowCircles { get; set; } = true;
    public bool ShowStateIcons { get; set; } = true;
    public bool ShowCargoPercent { get; set; } = true;
    public bool KeepInCells { get; set; }
    public string? LastProfileId { get; set; }

    /// <summary>Fold state of Профили sections: "profileId/moduleId" or "cargo/&lt;section&gt;" -> open. Missing = default.</summary>
    // shortcut: keys of deleted profiles/modules are never pruned, prune in Store.DeleteProfile if the file grows.
    public Dictionary<string, bool>? Open { get; set; }
}
