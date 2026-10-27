using System.Collections.Generic;

namespace MiningDrone
{
    /// <summary>Stands in for src\L.cs, which reads the game's string table: the key is the text.</summary>
    internal static class L
    {
        public static string T(string key) => key;

        public static string F(string key, params object?[] args) => key;
    }
}

namespace MiningDrone.Autofleet
{
    /// <summary>Stands in for the game half of Logistics (src\Autofleet\Logistics.cs).</summary>
    internal static partial class Logistics
    {
        // The real one names resources through the game's resource rules; cargo blockers are not under test here.
        public static void Problems(CargoPolicy cargo, List<string> blockers)
        {
        }
    }
}
