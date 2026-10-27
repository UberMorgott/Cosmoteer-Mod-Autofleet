using Cosmoteer.Localization;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>
/// Localized text from the mod's strings/&lt;lang&gt;.rules (mod.rules: StringsFolder = "strings"), via the game's
/// Cosmoteer.Localization.Strings (follows the game language, falls back to en). Pass full literal keys
/// ("MiningDrone/Title") so lookups hit the game's string cache without allocating.
/// </summary>
internal static class L
{
    public static string T(string key) => Strings.GetText(key);

    public static string F(string key, params object?[] args) => Strings.FormatText(key, args);

    public static string State(MinerState s) => s switch
    {
        MinerState.FindTarget => T("MiningDrone/StFindTarget"),
        MinerState.Mining => T("MiningDrone/StMining"),
        MinerState.FlyToBase => T("MiningDrone/StFlyToBase"),
        MinerState.Offload => T("MiningDrone/StOffload"),
        MinerState.Waiting => T("MiningDrone/StWaiting"),
        MinerState.Evading => T("MiningDrone/StEvading"),
        MinerState.Paused => T("MiningDrone/StPaused"),
        MinerState.CollectLoose => T("MiningDrone/StCollectLoose"),
        MinerState.Approach => T("MiningDrone/StApproach"),
        MinerState.Holding => T("MiningDrone/StHolding"),
        MinerState.Manual => T("MiningDrone/StManual"),
        MinerState.Stuck => T("MiningDrone/StStuck"),
        MinerState.Trading => T("MiningDrone/StTrading"),
        _ => s.ToString(),
    };

    public static string Module(ModuleType? t)
    {
        if (t == null)
        {
            return T("MiningDrone/NoModule");
        }
        foreach (ModuleDef d in Registry.All)
        {
            if (d.Type == t)
            {
                return T(d.NameKey);
            }
        }
        return t.ToString()!;
    }

    public static string Module(string typeId)
    {
        ModuleDef? d = Registry.Get(typeId);
        return d == null ? typeId : T(d.NameKey);
    }
}
