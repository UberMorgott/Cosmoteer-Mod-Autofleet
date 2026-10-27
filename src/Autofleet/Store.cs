using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiningDrone.Autofleet;

/// <summary>
/// Persistence. Three separate stores:
/// - profile library (reusable, global): Autofleet.profiles.json
/// - local presentation prefs: Autofleet.prefs.json
/// - fleet state (assignments): the working copy Autofleet.state.json, plus one snapshot per save file in
///   Autofleet.saves\ written when the game saves (save path + file write time), so loading any save restores the
///   exact assignments and provenance that existed at that save. A save without a matching snapshot falls back to
///   the working copy (the pre-Autofleet behaviour: settings by ship ObjectID).
/// The legacy MiningDrone.settings.json is migrated once and never deleted.
/// Writes are debounced and never happen on the per-frame path unless dirty.
/// </summary>
internal static class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static ProfileLibraryFile _lib = new();
    private static PrefsFile _prefs = new();
    private static FleetStateFile _state = new();
    private static bool _libDirty, _prefsDirty, _stateDirty;

    /// <summary>File schema this build writes. A file with a newer schema is read (unknown fields kept in Extra) but
    /// never overwritten, so a downgrade cannot destroy data written by a newer version.</summary>
    /// <remarks>2 = profiles are live (no revisions; ships store ProfileId instead of a pinned snapshot).</remarks>
    public const int SupportedSchema = 2;
    private static bool _libReadOnly, _stateReadOnly, _readFailed;
    private static readonly HashSet<string> _keepSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private static long _dirtySince;

    public static string Folder => Log.ModFolder;
    private static string LibPath => Path.Combine(Folder, "Autofleet.profiles.json");
    private static string PrefsPath => Path.Combine(Folder, "Autofleet.prefs.json");
    private static string StatePath => Path.Combine(Folder, "Autofleet.state.json");
    private static string SavesDir => Path.Combine(Folder, "Autofleet.saves");
    private static string LegacyPath => Path.Combine(Folder, "MiningDrone.settings.json");

    public static PrefsFile Prefs => _prefs;
    public static List<Profile> Profiles => _lib.Profiles;
    public static Dictionary<string, ShipAssignment> Assignments => _state.Assignments;

    /// <summary>Bumped when the profile list or any profile changes (UI caches key on it).</summary>
    public static int LibraryVersion { get; private set; }

    /// <summary>Bumped by every assignment edit (cross-ship dependencies such as anchor cycles).</summary>
    public static int EditVersion { get; private set; }

    /// <summary>Where the current fleet state came from (shown in the Settings tab / log).</summary>
    public static string StateSource { get; private set; } = "";

    public static void Load()
    {
        _readFailed = false;
        _lib = Read<ProfileLibraryFile>(LibPath) ?? new ProfileLibraryFile();
        _libReadOnly = _readFailed || Newer(_lib.Schema, LibPath);
        if (UpgradeLibrary(_lib))
        {
            MarkLibrary();
        }
        _prefs = Read<PrefsFile>(PrefsPath) ?? new PrefsFile();
        SeedStarters();
        DedupeModules();
        _readFailed = false;
        FleetStateFile? st = Read<FleetStateFile>(StatePath);
        bool stateFailed = _readFailed;
        if (st == null && File.Exists(LegacyPath))
        {
            st = MigrateLegacy();
            _stateDirty = _prefsDirty = true;
            _dirtySince = Environment.TickCount64;
        }
        _state = st ?? new FleetStateFile();
        _stateReadOnly = stateFailed || Newer(_state.Schema, StatePath);
        if (Normalize(_state, out bool dropped))
        {
            if (dropped)
            {
                BackupDropped(StatePath);
            }
            _stateDirty = true;
            StartDirty();
        }
        StateSource = "working";
    }

    private static bool Newer(int schema, string path)
    {
        if (schema <= SupportedSchema)
        {
            return false;
        }
        Log.Info($"{Path.GetFileName(path)} has schema {schema} (this build: {SupportedSchema}); it is used but not overwritten");
        return true;
    }

    /// <summary>Copies <paramref name="path"/> to path+suffix unless that copy already exists (first migration wins).
    /// False when the copy failed: the caller must not overwrite <paramref name="path"/> this session.</summary>
    private static bool BackupOnce(string path, string suffix)
    {
        try
        {
            if (File.Exists(path) && !File.Exists(path + suffix))
            {
                File.Copy(path, path + suffix);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("store-backup:" + Path.GetFileName(path), ex);
            return false;
        }
    }

    /// <summary>Overrides were dropped from a state read from <paramref name="path"/>: back the file up first, else
    /// keep the state read-only (used, never written) so the dropped data survives on disk.</summary>
    private static void BackupDropped(string path)
    {
        if (_stateReadOnly)
        {
            return;
        }
        if (BackupOnce(path, ".overrides.bak"))
        {
            Log.Info("Per-ship setting overrides dropped: every ship now runs its profile's settings (" + Path.GetFileName(path) + ".overrides.bak kept)");
        }
        else
        {
            _stateReadOnly = true;
        }
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex)
        {
            Log.Error("store-read:" + Path.GetFileName(path), ex);
            _readFailed = true; // e.g. an enum value from a newer version: keep the file, never overwrite it this session
            try
            {
                File.Copy(path, path + ".bad", overwrite: true); // keep the unreadable file for inspection
            }
            catch
            {
            }
            return null;
        }
    }

    private static void SeedStarters()
    {
        bool changed = false;
        foreach (string key in Registry.StarterKeys)
        {
            if (_lib.SeededStarters.Contains(key))
            {
                continue; // a starter the user deleted stays deleted
            }
            _lib.SeededStarters.Add(key);
            if (FindProfile("starter-" + key) == null)
            {
                _lib.Profiles.Add(Registry.CreateStarter(key));
            }
            changed = true;
        }
        if (changed)
        {
            MarkLibrary();
        }
    }

    /// <summary>
    /// Older builds let "Add module" add a type twice. The resolver only ever runs the first instance of a type
    /// (Resolver.Refresh, pass 1), so dropping the later ones changes no behaviour.
    /// </summary>
    private static void DedupeModules()
    {
        bool changed = false;
        var seen = new HashSet<string>();
        foreach (Profile p in _lib.Profiles)
        {
            seen.Clear();
            for (int i = 0; i < p.Modules.Count; i++)
            {
                if (!seen.Add(p.Modules[i].Type))
                {
                    // Runs from GameLoadInitializer, before game strings load: no localized names (L.T) here.
                    Log.Info($"Profile {(string.IsNullOrEmpty(p.Name) ? p.Id : p.Name)}: removed duplicate module {p.Modules[i].Type} (inactive second instance)");
                    p.Modules.RemoveAt(i--);
                    changed = true;
                }
            }
        }
        if (changed)
        {
            MarkLibrary();
        }
    }

    /// <summary>Schema 1 profiles carried Revision/Modified: drop exactly those keys (other unknown fields stay in
    /// Extra). Returns true when the file changed. A newer-schema file is left alone.</summary>
    private static bool UpgradeLibrary(ProfileLibraryFile lib)
    {
        if (lib.Schema >= SupportedSchema)
        {
            return false;
        }
        lib.Schema = SupportedSchema;
        foreach (Profile p in lib.Profiles)
        {
            if (p.Extra != null)
            {
                p.Extra.Remove("Revision");
                p.Extra.Remove("Modified");
                if (p.Extra.Count == 0)
                {
                    p.Extra = null;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Fills missing objects and migrates older assignments: the schema 1 pinned "Snapshot" becomes ProfileId (read
    /// live); per-ship logic overrides written by older builds (<see cref="DroppedOverrides"/>) are dropped, so the
    /// profile is the only source of logic settings. Other unknown fields stay in Extra. Returns true when anything
    /// was migrated (state must be rewritten); <paramref name="dropped"/> = overrides were removed.
    /// </summary>
    private static bool Normalize(FleetStateFile st, out bool dropped)
    {
        bool changed = false;
        dropped = false;
        if (st.Schema < SupportedSchema)
        {
            st.Schema = SupportedSchema;
            changed = true;
        }
        foreach (var kv in st.Assignments)
        {
            ShipAssignment a = kv.Value;
            a.ShipId = kv.Key;
            a.ProfileId ??= "";
            a.Bindings ??= new TargetBindings();
            a.Bindings.Anchor ??= new AnchorLink();
            a.Bindings.Zone ??= new WorkZone();
            // Schema 1 kept a per-ship copy of offload-to-anchor in Bindings; it lives in the profile.
            bool hasOta = a.Bindings.Extra?.Remove("OffloadToAnchor") == true;
            if (a.Bindings.Extra is { Count: 0 })
            {
                a.Bindings.Extra = null;
            }
            foreach (string k in DroppedOverrides)
            {
                if (a.Extra?.Remove(k) == true)
                {
                    dropped = true;
                }
            }
            if (a.Extra is { Count: 0 })
            {
                a.Extra = null;
            }
            if (a.Legacy is { } old)
            {
                if (a.ProfileId.Length == 0)
                {
                    a.ProfileId = old.ProfileId ?? "";
                }
                a.Legacy = null;
                changed = true;
            }
            changed |= hasOta;
        }
        return changed | dropped;
    }

    /// <summary>JSON keys of the per-ship logic overrides older builds wrote on assignments (no longer read).</summary>
    private static readonly string[] DroppedOverrides =
        { "ParamOverrides", "EnabledOverrides", "CargoOverride", "ResumeAfterManual", "MissingPolicy", "FollowDistance", "OffloadToAnchor" };

    // ------------------------------------------------------------------ profiles

    public static Profile? FindProfile(string? id)
    {
        if (id == null)
        {
            return null;
        }
        foreach (Profile p in _lib.Profiles)
        {
            if (p.Id == id)
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>Stand-in for an unknown profile id: no modules, so the ship does nothing and shows a blocker. Never
    /// edited, never stored.</summary>
    public static readonly Profile Missing = new();

    public static Profile ProfileFor(string? id) => FindProfile(id) ?? Missing;

    /// <summary>
    /// Deletes a profile (the UI refuses while a running ship uses it). Drafts (settings of ships that are not
    /// driven) move to the default profile (recreated if the library became empty). Enabled assignments of ships
    /// not running now (other save, stasis, destroyed) keep the id: they idle with a visible "profile not found".
    /// </summary>
    public static void DeleteProfile(Profile p)
    {
        _lib.Profiles.Remove(p);
        MarkLibrary();
        if (_prefs.LastProfileId == p.Id)
        {
            _prefs.LastProfileId = null;
            MarkPrefs();
        }
        foreach (ShipAssignment a in _state.Assignments.Values)
        {
            if (a.ProfileId == p.Id && !a.Enabled)
            {
                SetProfile(a, DefaultProfile());
            }
        }
    }

    /// <summary>Drag-drop in the library order: right before <paramref name="before"/>, else right after <paramref name="after"/>.</summary>
    public static void MoveProfile(Profile p, Profile? before, Profile? after)
    {
        if (SortMath.MoveNear(_lib.Profiles, p, before, after))
        {
            MarkLibrary();
        }
    }

    /// <summary>Swaps two profiles in the library. The list order is the saved order (no extra field), shown by the
    /// fleet-tree folders and the Профили list.</summary>
    public static void SwapProfiles(Profile a, Profile b)
    {
        if (Swap(_lib.Profiles, a, b))
        {
            MarkLibrary();
        }
    }

    private static bool Swap(List<Profile> l, Profile a, Profile b)
    {
        int i = l.IndexOf(a), j = l.IndexOf(b);
        if (i < 0 || j < 0 || i == j)
        {
            return false;
        }
        (l[i], l[j]) = (l[j], l[i]);
        return true;
    }

    public static Profile DefaultProfile()
    {
        Profile? p = FindProfile(_prefs.LastProfileId) ?? FindProfile("starter-" + Registry.StarterMiner);
        if (p == null)
        {
            if (_lib.Profiles.Count > 0)
            {
                return _lib.Profiles[0];
            }
            p = Registry.CreateStarter(Registry.StarterMiner);
            _lib.Profiles.Add(p);
            MarkLibrary();
        }
        return p;
    }

    public static void MarkLibrary()
    {
        LibraryVersion++;
        _libDirty = true;
        StartDirty();
    }

    public static void MarkPrefs()
    {
        _prefsDirty = true;
        StartDirty();
    }

    /// <summary>Any assignment edit: bumps its version (agent recompiles) and schedules a write.</summary>
    public static void Touch(ShipAssignment a)
    {
        a.Version++;
        EditVersion++;
        _stateDirty = true;
        StartDirty();
    }

    private static void StartDirty()
    {
        if (_dirtySince == 0)
        {
            _dirtySince = Environment.TickCount64;
        }
    }

    public static ShipAssignment? Find(string shipId) => _state.Assignments.TryGetValue(shipId, out var a) ? a : null;

    /// <summary>Draft assignment for a not-yet-automated ship (like the old per-ship settings created on selection).</summary>
    public static ShipAssignment GetOrCreateDraft(string shipId, float x, float y, int owner)
    {
        if (_state.Assignments.TryGetValue(shipId, out var a))
        {
            return a;
        }
        a = new ShipAssignment { ShipId = shipId, OwnerIndex = owner, ProfileId = DefaultProfile().Id };
        a.Bindings.Zone.X = x;
        a.Bindings.Zone.Y = y;
        _state.Assignments[shipId] = a;
        Touch(a);
        return a;
    }

    /// <summary>Binds the ship to <paramref name="p"/> (read live from then on); targets and zone stay.</summary>
    public static void SetProfile(ShipAssignment a, Profile p)
    {
        a.ProfileId = p.Id;
        Touch(a);
    }

    // ------------------------------------------------------------------ per-save snapshots

    private static string SnapshotPath(string savePath)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(savePath.ToLowerInvariant()));
        return Path.Combine(SavesDir, Convert.ToHexString(h, 0, 10) + ".json");
    }

    /// <summary>A snapshot of this save under another file name (builds up to 0.1.0 named them by SHA1), found by the
    /// save path recorded inside it; null when there is none.</summary>
    private static string? LegacySnapshotPath(string savePath)
    {
        if (!Directory.Exists(SavesDir))
        {
            return null;
        }
        foreach (string f in Directory.EnumerateFiles(SavesDir, "*.json"))
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(f));
                if (d.RootElement.ValueKind == JsonValueKind.Object && string.Equals(Str(d.RootElement, nameof(SaveSnapshotFile.SavePath)), savePath, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // unreadable file: not this save's snapshot
            }
        }
        return null;
    }

    /// <summary>Serialized fleet state captured at the moment a save started (main thread).</summary>
    public static string CaptureState() => JsonSerializer.Serialize(_state, Json);

    /// <summary>Writes the captured state as the snapshot bound to the save file that was just written.</summary>
    public static void WriteSaveSnapshot(string savePath, string capturedStateJson)
    {
        try
        {
            FleetStateFile st = JsonSerializer.Deserialize<FleetStateFile>(capturedStateJson, Json) ?? new FleetStateFile();
            var snap = new SaveSnapshotFile
            {
                SavePath = savePath,
                SaveWriteTicksUtc = File.Exists(savePath) ? File.GetLastWriteTimeUtc(savePath).Ticks : 0,
                State = st,
            };
            string target = SnapshotPath(savePath);
            // The snapshot this write replaces: under the current name, else an older-named one of the same save.
            string prev = File.Exists(target) ? target : LegacySnapshotPath(savePath) ?? target;
            if (_keepSnapshots.Contains(prev))
            {
                Log.Info($"Save snapshot {Path.GetFileName(prev)} holds dropped overrides that could not be backed up; not overwritten");
                return;
            }
            SaveSnapshotFile? old = null;
            if (File.Exists(prev))
            {
                _readFailed = false;
                old = Read<SaveSnapshotFile>(prev);
                if (_readFailed || (old != null && (old.Schema > SupportedSchema || (old.State?.Schema ?? 0) > SupportedSchema)))
                {
                    Log.Info($"Save snapshot {Path.GetFileName(prev)} is unreadable or from a newer version; not overwritten");
                    return;
                }
            }
            snap.Extra = old?.Extra; // keep wrapper fields a newer version may have added
            Directory.CreateDirectory(SavesDir);
            Write(target, snap); // temp file + move: a crash never leaves a truncated snapshot
            Log.Info($"Save snapshot written for {Path.GetFileName(savePath)} ({st.Assignments.Count} assignment(s))");
        }
        catch (Exception ex)
        {
            Log.Error("save-snapshot", ex);
        }
    }

    /// <summary>
    /// On load: use the snapshot bound to this exact save (same path and file write time), else keep the working
    /// copy. The chosen state becomes the working copy. Returns true when an exact snapshot was used.
    /// </summary>
    public static bool BindToSave(string? savePath)
    {
        StateSource = "working";
        if (string.IsNullOrEmpty(savePath))
        {
            return false;
        }
        string sp = SnapshotPath(savePath);
        if (!File.Exists(sp) && LegacySnapshotPath(savePath) is string legacy)
        {
            sp = legacy;
        }
        SaveSnapshotFile? snap = Read<SaveSnapshotFile>(sp);
        if (snap == null)
        {
            Log.Info($"No Autofleet snapshot for save {Path.GetFileName(savePath)}; using the working state");
            return false;
        }
        long ticks = File.Exists(savePath) ? File.GetLastWriteTimeUtc(savePath).Ticks : -1;
        if (!string.Equals(snap.SavePath, savePath, StringComparison.OrdinalIgnoreCase) || snap.SaveWriteTicksUtc != ticks)
        {
            Log.Info($"Autofleet snapshot for {Path.GetFileName(savePath)} does not match this save file (written by another session); using the working state");
            return false;
        }
        _state = snap.State ?? new FleetStateFile();
        bool newer = Newer(snap.Schema, sp) || Newer(_state.Schema, sp); // before Normalize upgrades an old schema
        Normalize(_state, out bool dropped);
        if (newer)
        {
            _stateReadOnly = true; // used as-is, but this build never writes a newer schema back
        }
        if (dropped && !newer && !BackupOnce(sp, ".overrides.bak"))
        {
            _keepSnapshots.Add(sp); // a later save into this slot would destroy the only copy of the dropped overrides
            _stateReadOnly = true;
        }
        _stateDirty = true;
        StartDirty();
        StateSource = "save";
        Log.Info($"Autofleet state bound to save {Path.GetFileName(savePath)} ({_state.Assignments.Count} assignment(s))");
        return true;
    }

    /// <summary>Drops runtime provenance after it was consumed (it belongs to the save it came from).</summary>
    public static void ClearRuntime()
    {
        foreach (ShipAssignment a in _state.Assignments.Values)
        {
            a.Runtime = null;
        }
    }

    // ------------------------------------------------------------------ flush

    /// <summary>Called from the throttled tick; flushes at most once per 2 s.</summary>
    public static void FlushIfDue(bool force = false)
    {
        if (!(_libDirty || _prefsDirty || _stateDirty) || (!force && Environment.TickCount64 - _dirtySince < 2000))
        {
            return;
        }
        _dirtySince = 0;
        if (_libDirty)
        {
            _libDirty = false;
            if (!_libReadOnly)
            {
                Write(LibPath, _lib);
            }
        }
        if (_prefsDirty)
        {
            _prefsDirty = false;
            Write(PrefsPath, _prefs);
        }
        if (_stateDirty)
        {
            _stateDirty = false;
            if (!_stateReadOnly)
            {
                Write(StatePath, _state);
            }
        }
    }

    private static void Write<T>(string path, T data)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("store-write:" + Path.GetFileName(path), ex);
        }
    }

    // ------------------------------------------------------------------ legacy migration

    /// <summary>Old per-ship settings (MiningDrone.settings.json) -> Шахтёр assignments (fleet command only).</summary>
    private static FleetStateFile MigrateLegacy()
    {
        var st = new FleetStateFile();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(LegacyPath));
            JsonElement root = doc.RootElement;
            _prefs.ShowCircles = Bool(root, "ShowCircles", true);
            _prefs.KeepInCells = Bool(root, "KeepInCells", false);
            _prefs.ShowStateIcons = Bool(root, "ShowStateIcons", true);
            Profile? miner = FindProfile("starter-" + Registry.StarterMiner);
            if (miner == null)
            {
                miner = Registry.CreateStarter(Registry.StarterMiner); // ships reference it live: it must be in the library
                _lib.Profiles.Add(miner);
                MarkLibrary();
            }
            if (root.TryGetProperty("Ships", out JsonElement ships) && ships.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty sp in ships.EnumerateObject())
                {
                    ShipAssignment a = MigrateShip(sp.Name, sp.Value, miner);
                    st.Assignments[sp.Name] = a;
                }
            }
            Log.Info($"Migrated {st.Assignments.Count} legacy ship setting(s) into Autofleet assignments (legacy file kept)");
        }
        catch (Exception ex)
        {
            Log.Error("migrate-legacy", ex);
        }
        return st;
    }

    private static ShipAssignment MigrateShip(string id, JsonElement s, Profile miner)
    {
        var a = new ShipAssignment { ShipId = id, Enabled = Bool(s, "Enabled", false), ProfileId = miner.Id };
        // Old per-ship logic settings (scope, thresholds, reactions, cargo selection) are not carried over: the ship
        // runs the starter miner profile as is. Only its fleet command (targets, circle, return-home) migrates.
        int task = Int(s, "Task", 0); // 0 Mine, 1 Escort, 2 ReturnHome
        float radius = Float(s, "Radius", 3000f);
        a.Bindings.Zone.X = Float(s, "CenterX", 0f);
        a.Bindings.Zone.Y = Float(s, "CenterY", 0f);
        a.Bindings.Zone.R = radius;
        a.Bindings.OffloadTarget = Str(s, "BaseShipId");
        a.Bindings.Anchor.TargetShipId = task == 1 ? Str(s, "EscortShipId") : null;
        a.Temp = task == 2 ? TempIntent.ReturnHome : TempIntent.None;

        return a;
    }

    /// <summary>True when the ship's resolved Mine or Loot scope is "in a circle" (its zone binding is in use).</summary>
    public static bool WorksInZone(ShipAssignment a) =>
        Resolver.Param(a, Registry.Mine, Registry.PScope) == (double)HarvestScope.Zone
        || Resolver.Param(a, Registry.Loot, Registry.PScope) == (double)HarvestScope.Zone;

    /// <summary>
    /// Offline invariants (tools/jitcheck): a ship reads every logic setting from its live profile (an old saved
    /// override is dropped on load, never applied); a profile edit reaches a bound ship's compiled config with no
    /// apply step; old files migrate to live binding, keep targets/zone and unknown fields.
    /// </summary>
    internal static string? SelfCheck()
    {
        var p = new Profile { Id = "selfcheck-p" };
        p.Modules.Add(new ModuleInstance { Id = "m1", Type = Registry.Offload, Params = { [Registry.PThreshold] = 0.9 } });
        _lib.Profiles.Add(p);
        try
        {
            // Old per-ship overrides (the stale CargoOverride bug): dropped on load, cargo/params/anchor come from the profile.
            p.Cargo.Collect = new List<string> { "iron", "hyperium" };
            p.Defaults.Anchor.Hysteresis = 2.5f;
            const string withOv = "{\"Schema\":2,\"Assignments\":{\"a\":{\"ProfileId\":\"selfcheck-p\",\"Zzz\":1,"
                + "\"ParamOverrides\":{\"m1\":{\"Threshold\":0.5}},\"EnabledOverrides\":{\"m1\":false},\"CargoOverride\":{\"Collect\":[\"iron\"]},"
                + "\"ResumeAfterManual\":false,\"MissingPolicy\":\"WorkWithout\",\"FollowDistance\":99,\"OffloadToAnchor\":false,"
                + "\"Bindings\":{\"OffloadTarget\":\"o\",\"Zone\":{\"R\":777},\"Anchor\":{\"TargetShipId\":\"t\",\"OffsetX\":3,\"Hysteresis\":9,\"FollowDistance\":99}}}}}";
            FleetStateFile ov = JsonSerializer.Deserialize<FleetStateFile>(withOv, Json)!;
            if (!Normalize(ov, out bool dropped) || !dropped)
            {
                return "saved overrides not reported as dropped";
            }
            ShipAssignment a = ov.Assignments["a"];
            if (!ReferenceEquals(a.Cargo, p.Cargo) || a.Cargo.Collect?.Contains("hyperium") != true)
            {
                return "ship with an old cargo override does not use the profile's cargo";
            }
            string ovJson = JsonSerializer.Serialize(ov, Json);
            foreach (string k in DroppedOverrides)
            {
                if (a.Extra?.ContainsKey(k) == true)
                {
                    return "dropped override still written: " + k;
                }
            }
            if (!ovJson.Contains("Zzz"))
            {
                return "override drop lost unknown fields";
            }
            if (a.Bindings.Anchor.TargetShipId != "t" || a.Bindings.OffloadTarget != "o" || a.Bindings.Zone.R != 777f)
            {
                return "override drop touched targets or zone";
            }
            var cfg = new AgentConfig();
            Resolver.Refresh(cfg, a);
            if (cfg.Threshold != 0.9f || !cfg.ResumeAfterManual || cfg.AnchorLink.Hysteresis != 2.5f || cfg.AnchorLink.FollowDistance != p.Defaults.Anchor.FollowDistance
                || cfg.AnchorLink.TargetShipId != "t" || cfg.AnchorLink.OffsetX != 3f || cfg.OffloadId != "o")
            {
                return "compiled config not from the profile (or lost the ship's targets)";
            }

            // Live: edit the profile, no apply call; the next Refresh of every ship on it sees it.
            var b = new ShipAssignment { ShipId = "selfcheck-b", ProfileId = p.Id };
            var cfgB = new AgentConfig();
            Resolver.Refresh(cfgB, b);
            p.Modules[0].Params[Registry.PThreshold] = 0.4;
            p.Defaults.Anchor.MissingPolicy = MissingAnchorPolicy.Rally;
            MarkLibrary();
            if (!Resolver.Refresh(cfg, a) || cfg.Threshold != 0.4f || cfg.AnchorLink.MissingPolicy != MissingAnchorPolicy.Rally
                || !Resolver.Refresh(cfgB, b) || cfgB.Threshold != 0.4f || cfgB.AnchorLink.MissingPolicy != MissingAnchorPolicy.Rally)
            {
                return "profile edit not seen live by both ships' configs";
            }
            (p.Cargo.KeepMin ??= new Dictionary<string, int>())["iron"] = 5;
            if (a.Cargo.KeepMin?["iron"] != 5)
            {
                return "ship cargo policy is not the live profile's";
            }

            // Migration: schema 1 pinned snapshot -> ProfileId; its per-ship anchor/offload copies are dropped; unknown kept.
            const string old = "{\"Schema\":1,\"Assignments\":{\"s1\":{\"Enabled\":true,\"Zzz\":5,"
                + "\"Snapshot\":{\"ProfileId\":\"selfcheck-p\",\"Revision\":3,\"Modules\":[],\"Defaults\":{\"OffloadToAnchor\":true,\"Anchor\":{\"MissingPolicy\":\"Rally\"}}},"
                + "\"Bindings\":{\"OffloadToAnchor\":false,\"Future\":7,\"Anchor\":{\"MissingPolicy\":\"Rally\",\"FollowDistance\":250}}}}}";
            FleetStateFile st = JsonSerializer.Deserialize<FleetStateFile>(old, Json)!;
            if (!Normalize(st, out _))
            {
                return "old state not reported as migrated";
            }
            ShipAssignment m = st.Assignments["s1"];
            if (m.ProfileId != p.Id || !ReferenceEquals(m.Profile, p) || m.Legacy != null || st.Schema != SupportedSchema)
            {
                return "old state not bound to its profile id";
            }
            string json = JsonSerializer.Serialize(st, Json);
            if (json.Contains("\"Snapshot\"") || json.Contains("Revision") || !json.Contains("Zzz") || !json.Contains("Future")
                || m.Bindings.Extra?.ContainsKey("OffloadToAnchor") == true)
            {
                return "migrated state dropped unknown fields or kept old ones: " + json;
            }
            var lib = JsonSerializer.Deserialize<ProfileLibraryFile>("{\"Schema\":1,\"Profiles\":[{\"Id\":\"x\",\"Revision\":4,\"Modified\":true,\"Fut\":1}]}", Json)!;
            if (!UpgradeLibrary(lib) || lib.Profiles[0].Extra is not { Count: 1 } ex || !ex.ContainsKey("Fut"))
            {
                return "library upgrade kept revision keys or dropped unknown fields";
            }

            // Profile order is list order: a swap survives a save/load round trip with unknown fields intact.
            var ord = JsonSerializer.Deserialize<ProfileLibraryFile>("{\"Profiles\":[{\"Id\":\"a\"},{\"Id\":\"b\"},{\"Id\":\"c\",\"Fut\":1}],\"Top\":2}", Json)!;
            if (!Swap(ord.Profiles, ord.Profiles[2], ord.Profiles[0]) || Swap(ord.Profiles, ord.Profiles[0], ord.Profiles[0]))
            {
                return "profile swap refused or self-swap accepted";
            }
            ord = JsonSerializer.Deserialize<ProfileLibraryFile>(JsonSerializer.Serialize(ord, Json), Json)!;
            string order = ord.Profiles[0].Id + ord.Profiles[1].Id + ord.Profiles[2].Id;
            if (order != "cba" || ord.Profiles[0].Extra?.ContainsKey("Fut") != true || ord.Extra?.ContainsKey("Top") != true)
            {
                return "profile order not persisted or unknown fields lost: " + order;
            }
            return null;
        }
        finally
        {
            _lib.Profiles.Remove(p);
        }
    }

    private static bool Bool(JsonElement e, string name, bool def) =>
        e.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : def;

    private static int Int(JsonElement e, string name, int def) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;

    private static float Float(JsonElement e, string name, float def) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : def;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? StrList(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var l = new List<string>();
        foreach (JsonElement x in v.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String)
            {
                l.Add(x.GetString()!);
            }
        }
        return l;
    }
}
