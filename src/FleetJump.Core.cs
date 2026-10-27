namespace MiningDrone;

internal enum JumpBlock
{
    None,
    NoFtl,
    NoControl,
    NoFuel,
}

/// <summary>
/// Fleet jump eligibility, kept free of game types so the test project compiles this file on its own.
/// </summary>
internal static partial class FleetJump
{
    internal static JumpBlock Block(bool hasFtl, bool canCommand, int cost, int fuel) =>
        !hasFtl ? JumpBlock.NoFtl : !canCommand ? JumpBlock.NoControl : cost > fuel ? JumpBlock.NoFuel : JumpBlock.None;

    /// <summary>Offline invariants, run by tools/jitcheck: ineligibility reasons and their order.</summary>
    internal static string? SelfCheck()
    {
        if (Block(true, true, 10, 10) != JumpBlock.None) return "exact fuel blocked";
        if (Block(true, true, 11, 10) != JumpBlock.NoFuel) return "short fuel allowed";
        if (Block(false, true, 0, 0) != JumpBlock.NoFtl) return "ship without FTL allowed";
        if (Block(false, false, 0, 0) != JumpBlock.NoFtl) return "no-FTL not reported first";
        if (Block(true, false, 99, 0) != JumpBlock.NoControl) return "uncommandable ship not reported before fuel";
        return null;
    }
}
