using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;

/// <summary>
/// PoE2 Offset Verifier
///
/// After a game patch, run this while PoE2 is running.
/// It reads your saved offsets, checks them against expected values,
/// and reports which ones survived the patch and which need re-discovery.
///
/// Usage:
///   dotnet run                     (checks all offsets against current game)
///   dotnet run --baseline          (records current values as the new baseline)
///   dotnet run --discover hp       (guided Cheat Engine session for HP offset)
/// </summary>
class OffsetVerifier
{
    // ── Win32 memory reading ─────────────────────────────────────────────────
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, int size, out int read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr hProc, IntPtr addr, byte[] buf, int size, out int written);

    const int PROCESS_ALL_ACCESS = 0x1F0FFF;

    // ── Offset manifest ──────────────────────────────────────────────────────
    // Fill these in as you discover them. Chain = list of offsets to follow.
    // Final entry is the offset to the actual value.
    static readonly OffsetEntry[] KnownOffsets = new[]
    {
        new OffsetEntry
        {
            Name        = "Player.Life.CurHP",
            Description = "Current player HP",
            ValueType   = "int32",
            Chain       = new long[] { /* fill after discovery, e.g. 0x04A2B8, 0x58, 0x10, 0x3C */ },
            ExpectedMin = 1,
            ExpectedMax = 99999,
            VerifyWith  = "Look at your HP on screen. Value here should match.",
        },
        new OffsetEntry
        {
            Name        = "Player.Life.MaxHP",
            Description = "Maximum player HP",
            ValueType   = "int32",
            Chain       = new long[] { /* fill after discovery */ },
            ExpectedMin = 1,
            ExpectedMax = 99999,
            VerifyWith  = "Should match the max HP shown in character screen.",
        },
        new OffsetEntry
        {
            Name        = "Player.Life.CurMana",
            Description = "Current player mana",
            ValueType   = "int32",
            Chain       = new long[] { },
            ExpectedMin = 0,
            ExpectedMax = 99999,
            VerifyWith  = "Use a skill, value should decrease.",
        },
        new OffsetEntry
        {
            Name        = "Player.Life.CurES",
            Description = "Current energy shield",
            ValueType   = "int32",
            Chain       = new long[] { },
            ExpectedMin = 0,
            ExpectedMax = 99999,
            VerifyWith  = "Take a hit to ES. Value should decrease.",
        },
        new OffsetEntry
        {
            Name        = "Player.Position.GridX",
            Description = "Player grid X coordinate",
            ValueType   = "float",
            Chain       = new long[] { },
            ExpectedMin = 1,
            ExpectedMax = 10000,
            VerifyWith  = "Walk east, value should increase. Walk west, decrease.",
        },
        new OffsetEntry
        {
            Name        = "Player.Position.GridY",
            Description = "Player grid Y coordinate",
            ValueType   = "float",
            Chain       = new long[] { },
            ExpectedMin = 1,
            ExpectedMax = 10000,
            VerifyWith  = "Walk north/south, value should change.",
        },
        new OffsetEntry
        {
            Name        = "Player.Level",
            Description = "Player character level",
            ValueType   = "int32",
            Chain       = new long[] { },
            ExpectedMin = 1,
            ExpectedMax = 100,
            VerifyWith  = "Should match level shown on character screen.",
        },
        new OffsetEntry
        {
            Name        = "ServerData.Gold",
            Description = "Current gold in inventory",
            ValueType   = "int32",
            Chain       = new long[] { },
            ExpectedMin = 0,
            ExpectedMax = int.MaxValue,
            VerifyWith  = "Buy something from a vendor. Value should decrease by exact cost.",
        },
        new OffsetEntry
        {
            Name        = "IngameState.AreaHash",
            Description = "Current area unique hash",
            ValueType   = "int32",
            Chain       = new long[] { },
            ExpectedMin = int.MinValue,
            ExpectedMax = int.MaxValue,
            VerifyWith  = "Change zones via waypoint. Value must change.",
        },
        new OffsetEntry
        {
            Name        = "ServerData.Latency",
            Description = "Network latency (ping) in ms",
            ValueType   = "int32",
            Chain       = new long[] { },
            ExpectedMin = 1,
            ExpectedMax = 9999,
            VerifyWith  = "Should roughly match ping shown in game overlay.",
        },
    };

    // ── Entry point ──────────────────────────────────────────────────────────
    static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var proc = FindPoe2Process();
        if (proc == null)
        {
            Warn("PoE2 process not found. Start the game and log in first.");
            return;
        }

        var handle = OpenProcess(PROCESS_ALL_ACCESS, false, proc.Id);
        if (handle == IntPtr.Zero)
        {
            Warn("Could not open process. Run this tool as Administrator.");
            return;
        }

        var moduleBase = GetModuleBase(proc);
        Step($"Attached to {proc.ProcessName} (PID {proc.Id})");
        Step($"Module base: 0x{moduleBase:X}");

        bool baseline = args.Length > 0 && args[0] == "--baseline";

        Console.WriteLine();
        Console.WriteLine("  ┌─────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  PoE2 Offset Verifier                                           │");
        Console.WriteLine("  │  Checks which offsets survived the latest game patch            │");
        Console.WriteLine("  └─────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        var results = new List<VerifyResult>();

        foreach (var entry in KnownOffsets)
        {
            var result = VerifyOffset(handle, moduleBase, entry);
            results.Add(result);
            PrintResult(result);
        }

        // Summary
        int good = 0, broken = 0, empty = 0;
        foreach (var r in results)
        {
            if (r.ChainEmpty) empty++;
            else if (r.Valid) good++;
            else broken++;
        }

        Console.WriteLine();
        Console.WriteLine($"  Results: {good} OK  |  {broken} BROKEN (need re-discovery)  |  {empty} not yet discovered");
        Console.WriteLine();

        if (broken > 0)
        {
            Console.WriteLine("  ── Broken offsets ──────────────────────────────────────────────");
            foreach (var r in results)
            {
                if (!r.ChainEmpty && !r.Valid)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  ✗  {r.Entry.Name}");
                    Console.ResetColor();
                    Console.WriteLine($"     Was reading: {r.ReadValue}  Expected range: [{r.Entry.ExpectedMin}, {r.Entry.ExpectedMax}]");
                    Console.WriteLine($"     Re-discover: {r.Entry.VerifyWith}");
                    Console.WriteLine();
                }
            }
        }

        CloseHandle(handle);

        if (baseline)
            SaveBaseline(results);
    }

    static VerifyResult VerifyOffset(IntPtr handle, long moduleBase, OffsetEntry entry)
    {
        var result = new VerifyResult { Entry = entry };

        if (entry.Chain == null || entry.Chain.Length == 0)
        {
            result.ChainEmpty = true;
            result.Status = "NOT YET DISCOVERED";
            return result;
        }

        try
        {
            long address = moduleBase;
            for (int i = 0; i < entry.Chain.Length - 1; i++)
            {
                address = ReadPointer(handle, address + entry.Chain[i]);
                if (address == 0)
                {
                    result.Valid = false;
                    result.Status = $"NULL pointer at chain step {i} (offset 0x{entry.Chain[i]:X})";
                    return result;
                }
            }

            // Final read
            long finalAddr = address + entry.Chain[^1];
            long value = entry.ValueType switch
            {
                "float"  => (long)ReadFloat(handle, finalAddr),
                "int64"  => ReadInt64(handle, finalAddr),
                _        => ReadInt32(handle, finalAddr),
            };

            result.ReadValue = value;
            result.Valid = value >= entry.ExpectedMin && value <= entry.ExpectedMax;
            result.Status = result.Valid ? "OK" : $"OUT OF RANGE (read {value}, expected [{entry.ExpectedMin}, {entry.ExpectedMax}])";
        }
        catch (Exception ex)
        {
            result.Valid = false;
            result.Status = $"READ ERROR: {ex.Message}";
        }

        return result;
    }

    static void PrintResult(VerifyResult r)
    {
        if (r.ChainEmpty)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  ○  ");
            Console.ResetColor();
            Console.WriteLine($"{r.Entry.Name,-35} (not discovered yet)");
            return;
        }

        if (r.Valid)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  ✓  ");
            Console.ResetColor();
            Console.WriteLine($"{r.Entry.Name,-35} = {r.ReadValue}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("  ✗  ");
            Console.ResetColor();
            Console.WriteLine($"{r.Entry.Name,-35} BROKEN — {r.Status}");
        }
    }

    // ── Memory helpers ───────────────────────────────────────────────────────
    static long ReadInt32(IntPtr handle, long address)
    {
        var buf = new byte[4];
        ReadProcessMemory(handle, (IntPtr)address, buf, 4, out _);
        return BitConverter.ToInt32(buf, 0);
    }

    static long ReadInt64(IntPtr handle, long address)
    {
        var buf = new byte[8];
        ReadProcessMemory(handle, (IntPtr)address, buf, 8, out _);
        return BitConverter.ToInt64(buf, 0);
    }

    static float ReadFloat(IntPtr handle, long address)
    {
        var buf = new byte[4];
        ReadProcessMemory(handle, (IntPtr)address, buf, 4, out _);
        return BitConverter.ToSingle(buf, 0);
    }

    static long ReadPointer(IntPtr handle, long address)
    {
        var buf = new byte[8];
        ReadProcessMemory(handle, (IntPtr)address, buf, 8, out _);
        return BitConverter.ToInt64(buf, 0);
    }

    static Process? FindPoe2Process()
    {
        foreach (var name in new[] { "PathOfExile2", "PathOfExile2Steam", "PathOfExileSteam" })
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0) return procs[0];
        }
        return null;
    }

    static long GetModuleBase(Process proc)
    {
        foreach (ProcessModule mod in proc.Modules)
            if (mod.ModuleName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase))
                return mod.BaseAddress.ToInt64();
        return proc.MainModule!.BaseAddress.ToInt64();
    }

    static void SaveBaseline(List<VerifyResult> results)
    {
        var doc = new Dictionary<string, object>();
        foreach (var r in results)
            if (!r.ChainEmpty && r.Valid)
                doc[r.Entry.Name] = r.ReadValue;

        File.WriteAllText("baseline.json", JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Step("Baseline saved to baseline.json");
    }

    static void Step(string msg) { Console.ForegroundColor = ConsoleColor.Cyan;  Console.WriteLine($"==> {msg}"); Console.ResetColor(); }
    static void Warn(string msg) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"[!] {msg}"); Console.ResetColor(); }
}

class OffsetEntry
{
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string ValueType   { get; set; } = "int32";  // int32, int64, float
    public long[] Chain       { get; set; } = Array.Empty<long>();
    public long ExpectedMin   { get; set; }
    public long ExpectedMax   { get; set; }
    public string VerifyWith  { get; set; } = "";
}

class VerifyResult
{
    public OffsetEntry Entry    { get; set; } = null!;
    public bool Valid           { get; set; }
    public bool ChainEmpty      { get; set; }
    public long ReadValue       { get; set; }
    public string Status        { get; set; } = "";
}
