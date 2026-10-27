using System;
using System.Collections.Generic;
using Cosmoteer.Data;
using Cosmoteer.Game;
using Cosmoteer.Game.Multiplayer.MPInputs;
using Cosmoteer.Resources;
using Cosmoteer.Ships.Parts;
using Cosmoteer.Ships.Parts.Resources;
using Cosmoteer.Ships.Parts.UI;
using MiningDrone.Autofleet;

namespace MiningDrone;

/// <summary>
/// Production module (D19, background, opt-in): vanilla factories (a part with the "on_off" UI toggle and a
/// ResourceConverter with CheckResourceLimits) whose output type has a keep-max on this ship are switched off at that
/// cap and back on below a lower watermark, through MPSetUITogglesInput (whitelisted: GameModeManager.cs:602). A zero
/// vanilla desire never stops a factory (ResourceConverter.cs:408), hence the toggle. Types without keep-max stay
/// untouched; Collect is not a production filter. A desire below keep-max caps output first (vanilla wins; noted).
/// </summary>
internal sealed partial class ShipAgent
{
    private static readonly ID<PartToggleGuiRules> OnOffToggle = new("on_off");
    private readonly List<PartUIToggle> _toggles = new();

    private void ProductionTick(GameRoot game)
    {
        CargoPolicy cargo = Assignment.Cargo;
        if (cargo.KeepMax == null || cargo.KeepMax.Count == 0)
        {
            return;
        }
        _toggles.Clear();
        PartUIToggle.GetAllOnShip(Ship, OnOffToggle, _toggles);
        MPSetUITogglesInput? input = null;
        string capped = "";
        foreach (PartUIToggle t in _toggles)
        {
            Part? part = t.Part;
            if (part == null || part.IsDestroyed || Ledger.Has(Ledger.Kind.Toggle, t))
            {
                continue;
            }
            int want = FactoryWant(part, cargo, ref capped);
            int mode = want == 0 ? 0 : t.Rules.Default != 0 ? t.Rules.Default : 1;
            if (want < 0 || t.IsToggleOn == (want == 1) || game.Mode.IsToggleChoiceLocked(part.Rules, t.Rules.ToggleID, mode, Ship))
            {
                continue;
            }
            input ??= new MPSetUITogglesInput();
            input.ToggleValues.Add((t, mode));
        }
        if (capped.Length > 0)
        {
            string note = L.F("MiningDrone/ProdCappedByKeep", capped);
            _logi = _logi.Length == 0 ? note : _logi + " · " + note;
        }
        if (input == null)
        {
            return;
        }
        // The toggle's Mode changes only when the input executes: hold each one until then (no re-send meanwhile).
        Func<bool> done = Ledger.Barrier(game);
        foreach ((PartUIToggle? t, int mode) in input.ToggleValues)
        {
            Ledger.Reserve(Ledger.Kind.Toggle, t, 1, done);
            Log.Info($"[{Id}] production: factory {t?.Part?.Rules.ID} {(mode == 0 ? "off" : "on")}");
        }
        game.NetManager.EnqueueInput(input);
    }

    /// <summary>1 = on, 0 = off, -1 = leave (not a managed factory, or between the watermarks). Off when any managed
    /// output reached its keep-max; on only when every managed output is below its low watermark.</summary>
    private int FactoryWant(Part part, CargoPolicy cargo, ref string capped)
    {
        bool managed = false, stop = false, low = true;
        foreach (PartComponent comp in part.Components)
        {
            if (comp is not ResourceConverter conv || !conv.Rules.CheckResourceLimits)
            {
                continue;
            }
            foreach (ResourceConverterRules.ResourceInfo to in conv.Rules.To)
            {
                if (!part.TryGetComponent(to.Storage, out IResourceStorage? s))
                {
                    continue;
                }
                ID<ResourceRules> rt = s.ResourceType;
                if (Logistics.EffectiveMax(Ship, rt, cargo) is not int max)
                {
                    continue;
                }
                managed = true;
                int desire = World.Keep(Ship, rt);
                if (desire > 0 && desire < max && capped.Length == 0)
                {
                    capped = World.IndexOf(rt) is int i && i >= 0 ? World.Resources[i].NameKey.Text : rt.ToString();
                }
                int state = FactoryState(Logistics.Holding(Ship, rt), max, to.Quantity.GetValue(part, 1f));
                stop |= state == 0;
                low &= state == 1;
            }
        }
        return !managed ? -1 : stop ? 0 : low ? 1 : -1;
    }
}
