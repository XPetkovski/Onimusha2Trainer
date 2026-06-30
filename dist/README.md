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

## What's actually wired up

The FearLess `.CT` you provided contained **only one real cheat — Infinite Arrows**
(plus an ad entry that pops a dialog pushing a "Mod Engine" download; the real
Health/Mana/Souls cheats are gated behind that and are **not** in the file). So:

| Hotkey | Cheat | Status |
|--------|-------|--------|
| **F4** | Infinite Arrows | ✅ wired from the table's real signature (code injection) |
| F1 | Infinite Health | ⛔ needs a signature (none in the .CT) |
| F2 | Infinite Magic | ⛔ needs a signature |
| F3 | Infinite Souls | ⛔ needs a signature |

F4 is the one you can test end-to-end right now. For F1–F3 you'll need to find the
signatures in Cheat Engine yourself (Step 2) or get a fuller table.

> The game is **64-bit** (`Onimusha2.exe`, MT Framework). The trainer auto-detects
> pointer width, so this is handled.

## Two cheat styles in this trainer

- **Value freeze** (`Cheat`) — resolves an address and overwrites it ~60×/sec.
  Good for health/magic/souls once you have a pointer or AOB for the *value*.
- **Code injection** (`Injection`) — the MT Framework way: AOB-scan the *instruction*
  that touches the value, allocate a code cave, and detour through it so the game
  keeps the value topped up. This is how Infinite Arrows works (there's no static
  pointer to the arrow byte; it lives at `[rcx+1C]` with `rcx` only valid at runtime).
  Injections are auto-reverted (bytes restored, cave freed) when you press END.

## Step 1 — process name (already set)

```csharp
const string ProcessName = "Onimusha2"; // image name from the .CT, no ".exe"
```

`GetProcessesByName` wants the image name (no `.exe`). Confirm in Task Manager →
**Details**; adjust if your release differs.

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