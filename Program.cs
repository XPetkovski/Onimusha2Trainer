using System.Runtime.InteropServices;
using Onimusha2Trainer;

// ── Configuration ───────────────────────────────────────────────────────────
// Real process name for the PC release (MT Framework engine). Process names may
// contain spaces but NOT the ".exe" suffix. Confirm in Task Manager → Details.
const string ProcessName = "Onimusha 2 Samurai's Destiny";

// Global hotkey virtual-key codes (work even while the game has focus).
const int VK_F1 = 0x70, VK_F2 = 0x71, VK_F3 = 0x72, VK_END = 0x23;

[DllImport("user32.dll")]
static extern short GetAsyncKeyState(int vKey);

// MT Framework tables (FearLess .CT) use AOB signatures, not static pointer
// chains. Fill AobPattern with the byte signature from the table; AobOperandOffset
// is where the 4-byte pointer operand sits inside the match. Until you paste real
// patterns these scans just fail to resolve and the trainer writes nothing.
//
// To find/verify a pattern, run:  Onimusha2Trainer.exe aob "8B 0D ?? ?? ?? ?? 89"
var cheats = new[]
{
    new Cheat
    {
        Name = "Infinite Health",
        ToggleKey = ConsoleKey.F1,
        Kind = ValueKind.Float,           // MT Framework health is typically a float
        AobPattern = null,                // e.g. "F3 0F 10 ?? ?? ?? ?? ?? 0F 2F"
        AobOperandOffset = 0,
        Offsets = [],
        FreezeValue = 999f,
    },
    new Cheat
    {
        Name = "Infinite Magic",
        ToggleKey = ConsoleKey.F2,
        Kind = ValueKind.Float,
        AobPattern = null,
        AobOperandOffset = 0,
        Offsets = [],
        FreezeValue = 999f,
    },
    new Cheat
    {
        Name = "Infinite Souls (money)",
        ToggleKey = ConsoleKey.F3,
        Kind = ValueKind.Int,             // souls are an integer
        AobPattern = null,
        AobOperandOffset = 0,
        Offsets = [],
        FreezeValue = 999999f,
    },
};

// ── Attach ───────────────────────────────────────────────────────────────────
Console.WriteLine($"Onimusha 2 Trainer — attaching to '{ProcessName}'...");
MemoryEditor mem;
try
{
    mem = MemoryEditor.Attach(ProcessName);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(ex.Message);
    Console.ResetColor();
    Console.WriteLine("\nPress any key to exit.");
    Console.ReadKey();
    return;
}

using (mem)
{
    Console.WriteLine($"Attached (pid {mem.Process.Id}, {(mem.Is64Bit ? "x64" : "x86")} pointers).");
    Console.WriteLine($"Module base: 0x{mem.ModuleBase.ToInt64():X}\n");

    // ── `aob` command: one-shot scan to lift/verify a pattern from a .CT ──────
    if (args.Length >= 2 && args[0].Equals("aob", StringComparison.OrdinalIgnoreCase))
    {
        var pattern = string.Join(' ', args[1..]);
        Console.WriteLine($"Scanning module for pattern:\n  {pattern}\n");
        var hit = mem.AobScan(pattern);
        if (hit == IntPtr.Zero)
        {
            Console.WriteLine("No match.");
        }
        else
        {
            long rel = hit.ToInt64() - mem.ModuleBase.ToInt64();
            Console.WriteLine($"Match at 0x{hit.ToInt64():X}  (module+0x{rel:X})");
            if (mem.TryRead<int>(hit, out var dword))
                Console.WriteLine($"First 4 bytes as int: {dword} (0x{(uint)dword:X8})");
        }
        return;
    }

    PrintHelp(cheats);

    if (cheats.All(c => c.AobPattern is null && c.StaticOffset == 0))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠ No cheats are configured yet — every AobPattern is null.");
        Console.WriteLine("  The trainer will attach and toggle, but write nothing until you");
        Console.WriteLine("  paste real AOB signatures (see README, Steps 2–3).\n");
        Console.ResetColor();
    }

    bool prevF1 = false, prevF2 = false, prevF3 = false;
    int frame = 0;

    while (true)
    {
        if (Pressed(VK_END)) break;

        ToggleOnEdge(VK_F1, ref prevF1, cheats[0]);
        ToggleOnEdge(VK_F2, ref prevF2, cheats[1]);
        ToggleOnEdge(VK_F3, ref prevF3, cheats[2]);

        foreach (var c in cheats)
            c.Apply(mem);

        // Refresh the status readout ~3×/sec so you can see resolution working.
        if (frame++ % 20 == 0)
            PrintStatus(mem, cheats);

        Thread.Sleep(16); // ~60 Hz; freezes the value before the game reads it back
    }

    Console.WriteLine("\nExiting. Cheats stopped.");
}

return;

bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

void ToggleOnEdge(int vk, ref bool prev, Cheat cheat)
{
    bool down = Pressed(vk);
    if (down && !prev)
        cheat.Active = !cheat.Active; // state is shown by the live status block
    prev = down;
}

static void PrintStatus(MemoryEditor mem, Cheat[] cheats)
{
    // Redraw a fixed status block in place (one line per cheat).
    int top = Math.Max(0, Console.CursorTop);
    foreach (var c in cheats)
    {
        bool resolved = c.TryReadCurrent(mem, out var value);
        string state = c.Active ? "ON " : "off";
        string addr = resolved ? $"value={value}" : "unresolved";
        Console.Write($"  [{state}] {c.Name,-26} {addr}".PadRight(Console.WindowWidth - 1));
        Console.WriteLine();
    }
    // Move the cursor back up so the next refresh overwrites this block.
    Console.SetCursorPosition(0, top);
}

static void PrintHelp(Cheat[] cheats)
{
    Console.WriteLine("Hotkeys (work while the game is focused):");
    foreach (var c in cheats)
        Console.WriteLine($"  {c.ToggleKey,-4}  toggle {c.Name}");
    Console.WriteLine("  END   quit");
    Console.WriteLine("\nTip: run  Onimusha2Trainer.exe aob \"<pattern>\"  to test a signature.\n");
}
