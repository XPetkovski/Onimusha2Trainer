namespace Onimusha2Trainer;

/// <summary>
/// A code-cave detour cheat — the way MT Framework tables implement "infinite X".
/// Instead of racing the game to overwrite a value, it rewrites the instruction
/// that touches the value so the game itself keeps it topped up.
///
/// On enable:
///   1. AOB-scan for the target instruction(s).
///   2. Allocate a cave in the target and fill it with:
///        [patch bytes] + [original stolen bytes] + [abs jmp back to site+len]
///   3. Overwrite the site with an abs jmp into the cave (NOP-padded to len).
/// On disable: restore the original bytes. On cleanup: disable + free the cave.
///
/// StolenLength must cover whole instructions and be ≥ 14 (room for the hook jump).
/// </summary>
public sealed class Injection
{
    public required string Name { get; init; }
    public required ConsoleKey ToggleKey { get; init; }

    /// <summary>AOB signature of the hooked region (from the .CT's aobscanmodule line).</summary>
    public required string AobPattern { get; init; }

    /// <summary>Machine code prepended in the cave, run before the original instructions
    /// (e.g. the bytes for <c>mov byte ptr [rcx+1C], 0x0A</c>).</summary>
    public required byte[] PatchBytes { get; init; }

    /// <summary>How many original bytes to relocate to the cave and overwrite at the site.
    /// Must land on an instruction boundary and be ≥ 14.</summary>
    public required int StolenLength { get; init; }

    public bool Active { get; private set; }
    public IntPtr Site { get; private set; }
    private IntPtr _cave;
    private byte[]? _original;
    private bool _prepared;
    private bool _failed;

    public string State => _failed ? "not found" : Active ? "ON " : "off";

    /// <summary>Scan, allocate, and stage the cave. Idempotent; safe to call repeatedly.</summary>
    private bool Prepare(MemoryEditor mem)
    {
        if (_prepared) return true;
        if (_failed) return false;

        if (StolenLength < 14)
            throw new InvalidOperationException($"{Name}: StolenLength must be ≥ 14 (room for the hook jump).");

        Site = mem.AobScan(AobPattern);
        if (Site == IntPtr.Zero)
        {
            _failed = true;
            return false;
        }

        _original = mem.ReadBytes(Site, StolenLength);

        _cave = mem.Alloc(0x1000);
        var jmpBack = MemoryEditor.AbsoluteJump(Site + StolenLength);

        var cave = new byte[PatchBytes.Length + StolenLength + jmpBack.Length];
        int o = 0;
        PatchBytes.CopyTo(cave, o); o += PatchBytes.Length;
        _original.CopyTo(cave, o); o += StolenLength;
        jmpBack.CopyTo(cave, o);
        mem.WriteBytes(_cave, cave);

        _prepared = true;
        return true;
    }

    public void Enable(MemoryEditor mem)
    {
        if (Active || !Prepare(mem)) return;

        var hook = new byte[StolenLength];
        var jmp = MemoryEditor.AbsoluteJump(_cave);
        jmp.CopyTo(hook, 0);
        for (int i = jmp.Length; i < hook.Length; i++)
            hook[i] = 0x90; // NOP padding

        var old = mem.Protect(Site, StolenLength, MemoryEditor.PAGE_EXECUTE_READWRITE);
        mem.WriteBytes(Site, hook);
        mem.Protect(Site, StolenLength, old);
        Active = true;
    }

    public void Disable(MemoryEditor mem)
    {
        if (!Active || _original is null) { Active = false; return; }

        var old = mem.Protect(Site, StolenLength, MemoryEditor.PAGE_EXECUTE_READWRITE);
        mem.WriteBytes(Site, _original);
        mem.Protect(Site, StolenLength, old);
        Active = false;
    }

    public void Toggle(MemoryEditor mem)
    {
        if (Active) Disable(mem);
        else Enable(mem);
    }

    /// <summary>Restore original bytes and free the cave. MUST run before exit — otherwise
    /// the game keeps jumping into a cave that no longer exists and will crash.</summary>
    public void Cleanup(MemoryEditor mem)
    {
        try { Disable(mem); } catch { /* best-effort */ }
        if (_cave != IntPtr.Zero)
        {
            mem.Free(_cave);
            _cave = IntPtr.Zero;
        }
    }
}
