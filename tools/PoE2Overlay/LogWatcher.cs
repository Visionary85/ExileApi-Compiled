using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PoE2Overlay;

/// <summary>
/// Watches PoE2's Client.txt for zone-change and level-up events.
/// Safe to subscribe from any thread; events are raised on a background thread.
/// </summary>
sealed class LogWatcher : IDisposable
{
    // "Generating level 12 area "g1_4"" — language-agnostic, stable across patches
    static readonly Regex GeneratingArea = new(
        @"Generating level \d+ area ""([^""]+)""", RegexOptions.Compiled);

    // Fallback "[SCENE] Set Source [Clearfell]" — used when areaId isn't available
    static readonly Regex SceneSource = new(
        @"\[SCENE\] Set Source \[(.+?)\]", RegexOptions.Compiled);

    // "PlayerName is now level 42"
    static readonly Regex LevelUp = new(
        @":\s+(\S+)\s+is now level (\d+)", RegexOptions.Compiled);

    // Ignore act title cards
    static readonly Regex ActTitle = new(@"^Act \d+$", RegexOptions.Compiled);

    public event Action<string, int>? ZoneChanged;   // (displayName, actId)
    public event Action<int>? LevelChanged;           // (newLevel)

    FileSystemWatcher? _fsw;
    Timer? _pollTimer;
    long _lastPos;
    string? _logPath;
    string? _lastZone;
    bool _disposed;

    public string? LogPath => _logPath;

    public void Start(string? customPath = null)
    {
        _logPath = customPath ?? FindLog();
        if (_logPath is null)
        {
            Console.Error.WriteLine("[PoE2Overlay] Client.txt not found — set a custom path via Settings.");
            return;
        }

        Console.WriteLine($"[PoE2Overlay] Watching: {_logPath}");

        // Scan the tail of the existing file for the current zone
        ScanTail(_logPath);

        _fsw = new FileSystemWatcher(Path.GetDirectoryName(_logPath)!, Path.GetFileName(_logPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _fsw.Changed += (_, _) => ReadNewLines();

        // Fallback poll every second (covers cases where FSW events are missed)
        _pollTimer = new Timer(_ => ReadNewLines(), null, 1000, 1000);
    }

    static string? FindLog()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                @"My Games\Path of Exile 2\logs\Client.txt"),
            @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile 2\logs\Client.txt",
            @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile 2\logs\Client.txt",
            @"C:\Program Files\Epic Games\PathOfExile2\logs\Client.txt",
            @"C:\Daum Games\Path of Exile2\logs\Client.txt",
            @"C:\Daum Games\Path of Exile2\logs\KakaoClient.txt",
        };

        foreach (var p in candidates)
            if (File.Exists(p)) return p;

        return null;
    }

    void ScanTail(string path)
    {
        try
        {
            var info = new FileInfo(path);
            long start = Math.Max(0, info.Length - 131_072); // last 128 KB
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string? lastZone = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var z = ParseZone(line);
                if (z is not null) lastZone = z;
            }

            _lastPos = fs.Position;

            if (lastZone is not null)
                EmitZone(lastZone);
        }
        catch { /* file locked or missing — handled by fallback poll */ }
    }

    readonly object _readLock = new();
    void ReadNewLines()
    {
        if (_logPath is null || _disposed) return;
        lock (_readLock)
        {
            try
            {
                using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long size = fs.Length;
                if (size < _lastPos) _lastPos = 0; // file was truncated / rotated

                if (size <= _lastPos) return;
                fs.Seek(_lastPos, SeekOrigin.Begin);

                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var zone = ParseZone(line);
                    if (zone is not null) EmitZone(zone);

                    var lvl = ParseLevel(line);
                    if (lvl > 0) LevelChanged?.Invoke(lvl);
                }

                _lastPos = fs.Position;
            }
            catch { /* ignore transient read errors */ }
        }
    }

    static string? ParseZone(string line)
    {
        var m = GeneratingArea.Match(line);
        if (m.Success)
        {
            var areaId = m.Groups[1].Value.TrimEnd('_');
            return QuestData.ResolveAreaId(areaId);
        }

        var m2 = SceneSource.Match(line);
        if (m2.Success)
        {
            var name = m2.Groups[1].Value;
            if (!ActTitle.IsMatch(name) && name != "(unknown)" && name != "(null)")
                return name;
        }

        return null;
    }

    static int ParseLevel(string line)
    {
        var m = LevelUp.Match(line);
        return m.Success ? int.Parse(m.Groups[2].Value) : 0;
    }

    void EmitZone(string zone)
    {
        if (zone == _lastZone) return;
        _lastZone = zone;
        var act = QuestData.GetAct(zone);
        ZoneChanged?.Invoke(zone, act);
    }

    public void Restart(string customPath)
    {
        _fsw?.Dispose();
        _pollTimer?.Dispose();
        _fsw = null;
        _pollTimer = null;
        _lastPos = 0;
        _lastZone = null;
        Start(customPath);
    }

    public void Dispose()
    {
        _disposed = true;
        _fsw?.Dispose();
        _pollTimer?.Dispose();
    }
}
