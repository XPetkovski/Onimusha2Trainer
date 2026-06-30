using System.Runtime.InteropServices;
using Onimusha2Trainer;

// ── Configuration ───────────────────────────────────────────────────────────
// Real process name for the PC release (MT Framework engine). Process names may
// contain spaces but NOT the ".exe" suffix. Confirm in Task Manager → Details.
const string ProcessName = "Onimusha2";

// Global hotkey virtual-key codes (work even while the game has focus).
const int VK_F1 = 0x70, VK_F2 = 0x71, VK_F3 = 0x72, VK_F4 = 0x73, VK_END = 0x23;

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

// Code-injection cheats (the way MT Framework tables really do "infinite X").
// Infinite Arrows is the one cheat actually present in the FearLess .CT — its
// signature and patch are taken verbatim from the table's auto-assembler script:
//   aobscanmodule(...,Onimusha2.exe,44 0F BE 79 1C 48 8B 87 C8 02 00 00 48 85 C0)
//   newmem: mov [rcx+1C], #10   (#10 = decimal 10 → byte 0x0A)
// The 15-byte signature covers three whole instructions, which we relocate.
var injections = new[]
{
    new Injection
    {
        Name = "Infinite Arrows",
        ToggleKey = ConsoleKey.F4,
        AobPattern = "44 0F BE 79 1C 48 8B 87 C8 02 00 00 48 85 C0",
        PatchBytes = [0xC6, 0x41, 0x1C, 0x0A], // mov byte ptr [rcx+1C], 0x0A
        StolenLength = 15,
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
        var hits = mem.AobScanAll(pattern, max: 64);
        if (hits.Count == 0)
        {
            Console.WriteLine("No match.");
        }
        else
        {
            Console.ForegroundColor = hits.Count == 1 ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine(hits.Count == 1
                ? "1 match (unique — good signature):"
                : $"{hits.Count} matches (NOT unique — narrow the pattern before using it):");
            Console.ResetColor();
            foreach (var hit in hits)
            {
                long rel = hit.ToInt64() - mem.ModuleBase.ToInt64();
                Console.WriteLine($"  0x{hit.ToInt64():X}  (module+0x{rel:X})");
            }
            if (hits.Count == 64)
                Console.WriteLine("  … (stopped at 64)");
        }
        return;
    }

    PrintHelp(cheats, injections);

    if (cheats.All(c => c.AobPattern is null && c.StaticOffset == 0))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("⚠ Health/Magic/Souls have no signatures yet (the .CT didn't contain them).");
        Console.WriteLine("  Only Infinite Arrows (F4) is wired up. The rest need signatures you find");
        Console.WriteLine("  in Cheat Engine or from a fuller table — see README.\n");
        Console.ResetColor();
    }

    bool prevF1 = false, prevF2 = false, prevF3 = false, prevF4 = false;
    int frame = 0;

    try
    {
        while (true)
        {
            if (Pressed(VK_END)) break;

            ToggleCheat(VK_F1, ref prevF1, cheats[0]);
            ToggleCheat(VK_F2, ref prevF2, cheats[1]);
            ToggleCheat(VK_F3, ref prevF3, cheats[2]);
            ToggleInjection(VK_F4, ref prevF4, injections[0]);

            foreach (var c in cheats)
                c.Apply(mem);

            // Refresh the status readout ~3×/sec so you can see resolution working.
            if (frame++ % 20 == 0)
                PrintStatus(mem, cheats, injections);

            Thread.Sleep(16); // ~60 Hz; freezes the value before the game reads it back
        }
    }
    finally
    {
        // Critical: restore patched instructions and free caves, or the game will
        // keep jumping into a cave that no longer exists and crash.
        foreach (var inj in injections)
            inj.Cleanup(mem);
        Console.WriteLine("\nExiting. Cheats stopped and injections reverted.");
    }
}

return;

bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

void ToggleCheat(int vk, ref bool prev, Cheat cheat)
{
    bool down = Pressed(vk);
    if (down && !prev)
        cheat.Active = !cheat.Active; // state is shown by the live status block
    prev = down;
}

void ToggleInjection(int vk, ref bool prev, Injection inj)
{
    bool down = Pressed(vk);
    if (down && !prev)
        inj.Toggle(mem);
    prev = down;
}

static void PrintStatus(MemoryEditor mem, Cheat[] cheats, Injection[] injections)
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
    foreach (var inj in injections)
        Console.Write($"  [{inj.State}] {inj.Name,-26} code injection".PadRight(Console.WindowWidth - 1) + "\n");
    // Move the cursor back up so the next refresh overwrites this block.
    Console.SetCursorPosition(0, top);
}

static void PrintHelp(Cheat[] cheats, Injection[] injections)
{
    Console.WriteLine("Hotkeys (work while the game is focused):");
    foreach (var c in cheats)
        Console.WriteLine($"  {c.ToggleKey,-4}  toggle {c.Name}");
    foreach (var inj in injections)
        Console.WriteLine($"  {inj.ToggleKey,-4}  toggle {inj.Name}");
    Console.WriteLine("  END   quit");
    Console.WriteLine("\nTip: run  Onimusha2Trainer.exe aob \"<pattern>\"  to test a signature.\n");
}
