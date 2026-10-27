using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

// Compiled against publicized game assemblies; CoreCLR honours this attribute so internal game
// members can be used directly (no reflection, no per-call overhead).
[assembly: IgnoresAccessChecksTo("Cosmoteer")]
[assembly: IgnoresAccessChecksTo("HalflingCore")]
[assembly: SupportedOSPlatform("windows7.0")]

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}
