# Onimusha 2 Trainer

A .NET 10 console trainer for **Onimusha 2: Samurai's Destiny** (PC / Windows 10).
Single-player only. **Back up your saves before freezing values** — writing a bad
value to the wrong address can corrupt a save.

## Files

| File | Role |
|------|------|
| `MemoryEditor.cs` | Win32 process-memory layer: read/write typed values, resolve pointer chains, AOB scan. |
| `Cheat.cs` | One freezable value (address info + freeze value + kind). |
| `Program.cs` | Config + global-hotkey loop (F1/F2/F3 toggle, END quits). |

No `unsafe` blocks needed — it uses `MemoryMarshal`. Build with `dotnet build`.
**Run the built exe as Administrator** (`OpenProcess` needs full access).

The PC release runs on **MT Framework**, whose cheat tables work by **AOB
(byte-pattern) signatures + code injection**, not static pointer chains. So the
durable, transferable "address" is a byte pattern — that's the addressing mode this
trainer is built around (`Cheat.AobPattern`). Static pointer chains still work via
`Cheat.StaticOffset`/`Offsets` if you find them, but AOB is the right format for MT
Framework and survives restarts/patches better.

> **Get tables only from `fearlessrevolution.com` or `opencheattables.com`.** A real
> Cheat Engine table is a `.CT` file you open *inside* Cheat Engine. Sites that hand
> you an archive with an `.exe` to run (fearlesscheatengine.net, cheatenginetable.net,
> flingcheatengine.com, etc.) are SEO clones that commonly bundle malware.

## Step 1 — process name (already set)

```csharp
const string ProcessName = "Onimusha 2 Samurai's Destiny"; // no ".exe"
```

`GetProcessesByName` wants the image name (spaces OK, no `.exe`). Confirm yours in
Task Manager → **Details**; adjust if your release differs.

## Step 2 — get the AOB signatures

Two routes:

- **Lift from a FearLess `.CT`** — open the table in Cheat Engine, inspect each
  entry's auto-assembler script / pointer; the `aobscan(name, AA BB ?? ...)` line is
  the signature. Copy that byte pattern.
- **Find your own** — scan for the value the classic way (souls: exact-value;
  health/magic: unknown→decreased/increased), then in the memory viewer look at the
  instruction that writes it (right-click address → *Find out what writes to this*),
  and use the surrounding bytes as the pattern (mask volatile operand bytes with `??`).

Verify any pattern against the live game with the built-in command:

```
Onimusha2Trainer.exe aob "F3 0F 10 ?? ?? ?? ?? ?? 0F 2F"
```

It prints the match address (and `module+offset`), or `No match`. A good signature
matches **exactly once**.

## Step 3 — fill in each `Cheat` in `Program.cs`

- `AobPattern` = the signature (use `??` for wildcard bytes).
- `AobOperandOffset` = byte position of the 4-byte pointer operand inside the match
  (e.g. `A1 disp32` → 1; `8B 0D disp32` → 2). The trainer reads that operand to get
  the base, then walks `Offsets`.
- `AobDereferenceOperand` = `false` if the pattern lands directly on the data instead
  of on an instruction operand.
- `Offsets` = pointer chain from the resolved base, or `[]`.
- `Kind` = `ValueKind.Int` (souls) or `ValueKind.Float` (health/magic). If a value
  misbehaves, try the other kind.
- `FreezeValue` = what to write continuously.

The AOB scan runs **once** and caches the resolved base (the instruction and the
global pointer slot it references are static for the process lifetime); the per-frame
loop only re-walks `Offsets`, so it stays cheap.

## Step 4 — run

Build, run the exe **as Administrator** with the game open:

```
F1   toggle Infinite Health
F2   toggle Infinite Magic
F3   toggle Infinite Souls
END  quit
```

Hotkeys are global (`GetAsyncKeyState`), so they work while the game is focused.

## Don't want to touch Cheat Engine?

Two ready-made options cover these cheats already: the **FearLess Revolution** `.CT`
(Infinite Health/Mana/Red Souls, Max Onimusha Orbs, Infinite Onimusha Mode time,
Infinite Money/Arrows), or **PLITCH**, a maintained commercial trainer app (most
cheats paid). This custom trainer is mainly worth it if you want your own tool.