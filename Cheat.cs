namespace Onimusha2Trainer;

public enum ValueKind { Int, Float }

/// <summary>
/// One freezable value. Pick ONE addressing mode:
///
///  • Static mode  — set <see cref="StaticOffset"/> (+ optional <see cref="Offsets"/>).
///    The classic pointer-scan result: green "OM2.exe+XXXXX" minus module base.
///
///  • AOB mode     — set <see cref="AobPattern"/> (+ <see cref="AobOperandOffset"/>).
///    The patch-resistant format MT Framework tables (FearLess .CT) use. The
///    pattern should match the instruction that references the value's base
///    pointer; we read the 4-byte operand to get that base, then walk Offsets.
///
/// The AOB scan runs once and the resolved base is cached (the instruction and
/// the global pointer slot it references are static for the process lifetime),
/// so the per-frame freeze only re-walks the Offsets chain — no rescanning.
/// </summary>
public sealed class Cheat
{
    public required string Name { get; init; }
    public required ConsoleKey ToggleKey { get; init; }
    public ValueKind Kind { get; init; } = ValueKind.Int;

    /// <summary>Value continuously written while the cheat is active.</summary>
    public float FreezeValue { get; init; }

    // ── Static addressing ────────────────────────────────────────────────────
    public int StaticOffset { get; init; }

    /// <summary>Pointer-scan offset chain. Empty = the base address holds the value directly.</summary>
    public int[] Offsets { get; init; } = [];

    // ── AOB addressing (takes precedence when AobPattern is set) ──────────────
    public string? AobPattern { get; init; }

    /// <summary>Byte position within the match where the 4-byte address operand starts
    /// (e.g. for <c>A1 disp32</c> use 1; for <c>8B 0D disp32</c> use 2).</summary>
    public int AobOperandOffset { get; init; }

    /// <summary>x86 absolute addressing: read a 4-byte pointer at (match+operand) to get
    /// the base. Set false if the pattern lands directly on the data and the match
    /// address itself is the base.</summary>
    public bool AobDereferenceOperand { get; init; } = true;

    public bool Active { get; set; }

    private bool _anchorResolved;
    private IntPtr _anchor = IntPtr.Zero;
    private bool _anchorFailed;

    /// <summary>
    /// The static base for the pointer walk. For static mode it's ModuleBase+StaticOffset.
    /// For AOB mode it's resolved (and cached) from the byte-pattern scan.
    /// </summary>
    private bool TryGetAnchor(MemoryEditor mem, out IntPtr anchor)
    {
        if (_anchorResolved) { anchor = _anchor; return true; }
        if (_anchorFailed) { anchor = IntPtr.Zero; return false; }

        if (AobPattern is null)
        {
            _anchor = mem.ModuleBase + StaticOffset;
            _anchorResolved = true;
            anchor = _anchor;
            return true;
        }

        var match = mem.AobScan(AobPattern);
        if (match == IntPtr.Zero)
        {
            _anchorFailed = true; // pattern not found in this build; stop retrying
            anchor = IntPtr.Zero;
            return false;
        }

        if (AobDereferenceOperand)
        {
            // x86 absolute operand: 4-byte address baked into the instruction.
            if (!mem.TryRead<int>(match + AobOperandOffset, out var abs))
            {
                _anchorFailed = true;
                anchor = IntPtr.Zero;
                return false;
            }
            _anchor = new IntPtr(unchecked((uint)abs));
        }
        else
        {
            _anchor = match;
        }

        _anchorResolved = true;
        anchor = _anchor;
        return true;
    }

    /// <summary>Resolve the full chain to the value's current address.</summary>
    public bool TryResolveAddress(MemoryEditor mem, out IntPtr addr)
    {
        addr = IntPtr.Zero;
        if (!TryGetAnchor(mem, out var anchor)) return false;
        return mem.TryWalk(anchor, Offsets, out addr) && addr != IntPtr.Zero;
    }

    /// <summary>Resolve the chain and overwrite the value. Silent no-op if the chain isn't currently valid.</summary>
    public void Apply(MemoryEditor mem)
    {
        if (!Active) return;
        if (!TryResolveAddress(mem, out var addr)) return;

        switch (Kind)
        {
            case ValueKind.Int:
                mem.Write(addr, (int)FreezeValue);
                break;
            case ValueKind.Float:
                mem.Write(addr, FreezeValue);
                break;
        }
    }

    public bool TryReadCurrent(MemoryEditor mem, out string display)
    {
        display = "?";
        if (!TryResolveAddress(mem, out var addr)) return false;

        if (Kind == ValueKind.Int && mem.TryRead<int>(addr, out var i)) { display = i.ToString(); return true; }
        if (Kind == ValueKind.Float && mem.TryRead<float>(addr, out var f)) { display = f.ToString("0.##"); return true; }
        return false;
    }
}
