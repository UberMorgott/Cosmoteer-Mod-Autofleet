using System;
using System.Collections.Generic;
using System.IO;

namespace MiningDrone;

/// <summary>Tiny append-only file log next to the mod DLL. Warnings are rate-limited per key.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> LastByKey = new();
    private static string? _path;

    public static string ModFolder { get; } =
        Path.GetDirectoryName(typeof(Log).Assembly.Location) ?? AppContext.BaseDirectory;

    public static void Init()
    {
        _path = Path.Combine(ModFolder, "Autofleet.log");
        try
        {
            File.WriteAllText(_path, $"[{DateTime.Now:HH:mm:ss}] Autofleet log start{Environment.NewLine}");
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string msg) => Write("INF", msg);

    /// <summary>Decision trace; only called on state changes / command issues (not per frame).</summary>
    public static void Debug(string shipId, string msg) => Write("DBG", "[" + shipId + "] " + msg);

    public static void Error(string key, Exception ex)
    {
        // Same failure every frame must not spam the disk (or cost FPS): once per 30 s per key.
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (LastByKey.TryGetValue(key, out long last) && now - last < 30_000)
            {
                return;
            }
            LastByKey[key] = now;
        }
        Write("ERR", key + ": " + ex);
    }

    private static void Write(string level, string msg)
    {
        if (_path == null)
        {
            return;
        }
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"[{DateTime.Now:HH:mm:ss}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
