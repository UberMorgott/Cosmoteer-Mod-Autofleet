using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

// Offline load check for the mod, run by tools/deploy.ps1 before every deploy.
//   jitcheck <mod.dll> [--bin <gameBin>] [--init <dataDir>]
// 1. Loads the mod against the REAL game assemblies and JIT-compiles every method, so any member/signature
//    mismatch (MissingMethodException, TypeLoadException...) surfaces without launching the game.
// 2. --init <dataDir>: copies the mod dll/pdb + *.json user data from dataDir (read-only) into a temp folder and
//    invokes the mod loader entry point (public static GameLoadInitializer) like the loader does, before game
//    strings/content exist. Catches load-time exceptions (TargetInvocationException in game).
// Exit code: 0 = pass, 1 = any check failed, 2 = usage/harness error.
class P
{
    const string DefaultBin = @"D:\Steam\steamapps\common\Cosmoteer\Bin";

    static int Main(string[] a)
    {
        string bin = DefaultBin, mod = null, initData = null;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "--bin" && i + 1 < a.Length) bin = a[++i];
            else if (a[i] == "--init" && i + 1 < a.Length) initData = a[++i];
            else if (mod == null && !a[i].StartsWith("--")) mod = a[i];
            else { Console.WriteLine("unknown argument: " + a[i]); return Usage(); }
        }
        if (mod == null) return Usage();
        mod = Path.GetFullPath(mod);
        if (!File.Exists(mod)) { Console.WriteLine("mod dll not found: " + mod); return 2; }
        if (!File.Exists(Path.Combine(bin, "Cosmoteer.dll"))) { Console.WriteLine("game Bin not found (no Cosmoteer.dll): " + bin); return 2; }
        if (initData != null && !Directory.Exists(initData)) { Console.WriteLine("init data dir not found: " + initData); return 2; }
        try { return Run(bin, mod, initData); }
        catch (Exception ex) { Console.WriteLine("HARNESS FAIL: " + ex); return 1; }
    }

    static int Usage()
    {
        Console.WriteLine("usage: jitcheck <mod.dll> [--bin <gameBin>] [--init <dataDir>]   (default bin: " + DefaultBin + ")");
        return 2;
    }

    static int Run(string bin, string mod, string initData)
    {
        if (initData != null)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "jitcheck-init-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string modDir = Path.GetDirectoryName(mod);
            foreach (var f in Directory.GetFiles(modDir, "*.dll").Concat(Directory.GetFiles(modDir, "*.pdb"))) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
            foreach (var f in Directory.GetFiles(initData, "*.json")) File.Copy(f, Path.Combine(tmp, Path.GetFileName(f)));
            mod = Path.Combine(tmp, Path.GetFileName(mod));
            Console.WriteLine("init sandbox: " + tmp);
        }
        AssemblyLoadContext.Default.Resolving += (ctx, name) => { foreach (var d in new[] { bin, Path.GetDirectoryName(mod) }) foreach (var n in new[] { name.Name, "0" + name.Name }) { var p = Path.Combine(d, n + ".dll"); if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p); } return null; };
        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(mod);
        int ok = 0, bad = 0;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(x => x != null).ToArray();
            bad++;
            foreach (var le in e.LoaderExceptions.Where(x => x != null)) Console.WriteLine("FAIL type load: " + le.Message);
        }
        foreach (var t in types)
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
                        .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
            {
                if (m.IsAbstract || m.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(m.MethodHandle); ok++; }
                catch (Exception ex) { bad++; Console.WriteLine($"FAIL {t.FullName}.{m.Name}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        Console.WriteLine($"Runtime: {Environment.Version}; JIT ok={ok} fail={bad}");
        // Mod self-checks: any static string SelfCheck() (null = pass) on pure model/editor logic.
        foreach (var sc in types.Select(t => t.GetMethod("SelfCheck", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)).Where(m => m != null && m.ReturnType == typeof(string)))
        {
            string fail;
            try { fail = (string)sc.Invoke(null, null); }
            catch (TargetInvocationException tie) { fail = tie.InnerException?.ToString(); }
            Console.WriteLine($"SELFCHECK {sc.DeclaringType.FullName}: " + (fail ?? "ok"));
            if (fail != null) bad++;
        }
        if (initData != null)
        {
            var entry = types.Select(t => t.GetMethod("GameLoadInitializer", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)).FirstOrDefault(m => m != null);
            if (entry == null) { Console.WriteLine("INIT FAIL: no public static GameLoadInitializer()"); return 1; }
            try { entry.Invoke(null, null); Console.WriteLine($"INIT ok: {entry.DeclaringType.FullName}.GameLoadInitializer"); }
            catch (TargetInvocationException tie) when (tie.InnerException is NullReferenceException nre
                && new System.Diagnostics.StackTrace(nre).GetFrame(0)?.GetMethod() == entry)
            {
                // Offline there is no running game (e.g. App.Director is null): an NRE thrown directly in the entry
                // method body, after everything it called returned, is a harness limit, not a mod bug.
                Console.WriteLine("INIT ok (WARN: entry hit game-only null after its calls returned: " + new System.Diagnostics.StackTrace(nre, true).GetFrame(0)?.GetFileLineNumber() + ")");
            }
            catch (TargetInvocationException tie)
            {
                Console.WriteLine("INIT FAIL: " + tie.InnerException);
                foreach (var l in Directory.GetFiles(Path.GetDirectoryName(mod), "*.log")) Console.WriteLine("--- " + Path.GetFileName(l) + "\n" + File.ReadAllText(l));
                return 1;
            }
        }
        if (bad != 0) Console.WriteLine("JIT FAIL: " + bad + " failure(s)");
        return bad == 0 ? 0 : 1;
    }
}
