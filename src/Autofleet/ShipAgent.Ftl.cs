using MiningDrone.Autofleet;

namespace MiningDrone;

internal sealed partial class ShipAgent
{
    /// <summary>
    /// Fleet jump (FleetJump.Send): hand the ship to the vanilla gate order without issuing anything. Forced Manual (whatever
    /// ResumeAfterManual says): the Manual branch of Tick holds while the gate order is displayed and returns before any
    /// arbitration; a cancelled order resumes after the usual 5 s quiet, and arrival builds a fresh agent in the new sim
    /// (MinerManager.OnSimChanged). <paramref name="now"/> is simulation time in ms, as in MinerManager.Tick.
    /// </summary>
    public void HoldForJump(long now)
    {
        if (State == MinerState.Paused || Assignment.UserPaused)
        {
            return; // an explicit pause stays a pause
        }
        // A pending save-restore must not overwrite the hold on the next tick (ShipAgent.cs:339).
        _reconcile = null;
        Assignment.Runtime = null;
        EndStuck();
        ForgetCommand();
        Target = null;
        SetOwner(null, "");
        _manualQuietSince = now;
        SetState(MinerState.Manual, L.T("MiningDrone/JumpHold"));
    }
}
