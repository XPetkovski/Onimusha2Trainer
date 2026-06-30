using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Onimusha2Trainer;

/// <summary>
/// Thin wrapper over the Win32 process-memory API. Attaches to a target
/// process and supports reading/writing typed values, resolving pointer
/// chains (module base + offsets), and AOB (byte-pattern) scanning.
/// </summary>
public sealed class MemoryEditor : IDisposable
{
    [Flags]
    private enum ProcessAccess : uint
    {
        VmOperation = 0x0008,
        VmRead = 0x0010,
        VmWrite = 0x0020,
        QueryInformation = 0x0400,
        AllForRw = VmOperation | VmRead | VmWrite | QueryInformation
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(ProcessAccess dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
        byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress,
        UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress,
        UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress,
        UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_EXECUTE_READWRITE = 0x40;

    private readonly IntPtr _handle;

    public Process Process { get; }

    /// <summary>Base address of the main module (the .exe) in the target's address space.</summary>
    public IntPtr ModuleBase { get; }

    public bool Is64Bit { get; }

    private MemoryEditor(Process process, IntPtr handle, IntPtr moduleBase, bool is64Bit)
    {
        Process = process;
        _handle = handle;
        ModuleBase = moduleBase;
        Is64Bit = is64Bit;
    }

    /// <summary>
    /// Attach by process name (without ".exe"). Throws if the process isn't
    /// found or OpenProcess fails (usually means "run as Administrator").
    /// </summary>
    public static MemoryEditor Attach(string processName)
    {
        var proc = Process.GetProcessesByName(processName).FirstOrDefault()
                   ?? throw new InvalidOperationException(
                       $"Process '{processName}' not found. Is the game running? " +
                       "Verify the name in Task Manager → Details.");

        var handle = OpenProcess(ProcessAccess.AllForRw, false, proc.Id);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "OpenProcess failed. Run this trainer as Administrator.");

        var moduleBase = proc.MainModule?.BaseAddress
                         ?? throw new InvalidOperationException("Could not read main module base address.");

        // 32-bit games (Onimusha 2 PC is x86) marshal pointers as 4 bytes.
        bool is64 = Environment.Is64BitOperatingSystem && !IsWow64(handle);
        return new MemoryEditor(proc, handle, moduleBase, is64);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr hProcess, out bool wow64Process);

    private static bool IsWow64(IntPtr handle) =>
        IsWow64Process(handle, out var wow64) && wow64;

    public T Read<T>(IntPtr address) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        if (!ReadProcessMemory(_handle, address, buffer, size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadProcessMemory @ 0x{address:X} failed.");
        return MemoryMarshal.Read<T>(buffer);
    }

    public bool TryRead<T>(IntPtr address, out T value) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        if (ReadProcessMemory(_handle, address, buffer, size, out _))
        {
            value = MemoryMarshal.Read<T>(buffer);
            return true;
        }
        value = default;
        return false;
    }

    public void Write<T>(IntPtr address, T value) where T : unmanaged
    {
        int size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        MemoryMarshal.Write(buffer, in value);
        if (!WriteProcessMemory(_handle, address, buffer, size, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteProcessMemory @ 0x{address:X} failed.");
    }

    public byte[] ReadBytes(IntPtr address, int count)
    {
        var buffer = new byte[count];
        if (!ReadProcessMemory(_handle, address, buffer, count, out var read) || (int)read != count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReadBytes @ 0x{address:X} ({count} bytes) failed.");
        return buffer;
    }

    public void WriteBytes(IntPtr address, byte[] data)
    {
        if (!WriteProcessMemory(_handle, address, data, data.Length, out var written) || (int)written != data.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteBytes @ 0x{address:X} ({data.Length} bytes) failed.");
    }

    /// <summary>Allocate executable memory inside the target (a "code cave").</summary>
    public IntPtr Alloc(int size)
    {
        var p = VirtualAllocEx(_handle, IntPtr.Zero, (UIntPtr)size, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
        if (p == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualAllocEx failed.");
        return p;
    }

    public void Free(IntPtr address)
    {
        if (address != IntPtr.Zero)
            VirtualFreeEx(_handle, address, UIntPtr.Zero, MEM_RELEASE);
    }

    /// <summary>Change page protection; returns the previous protection value.</summary>
    public uint Protect(IntPtr address, int size, uint newProtect)
    {
        if (!VirtualProtectEx(_handle, address, (UIntPtr)size, newProtect, out var old))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"VirtualProtectEx @ 0x{address:X} failed.");
        return old;
    }

    /// <summary>
    /// Build a position-independent 64-bit absolute jump (14 bytes):
    /// <c>FF 25 00 00 00 00</c> = <c>jmp qword ptr [rip+0]</c>, followed by the
    /// 8-byte target stored inline. Works regardless of distance to the target.
    /// </summary>
    public static byte[] AbsoluteJump(IntPtr target)
    {
        var b = new byte[14];
        b[0] = 0xFF;
        b[1] = 0x25; // jmp [rip+disp32], disp32 = 0
        // b[2..5] already zero
        BitConverter.GetBytes(target.ToInt64()).CopyTo(b, 6);
        return b;
    }

    /// <summary>
    /// Convenience: resolve a chain anchored at (ModuleBase + staticOffset).
    /// </summary>
    public bool TryResolve(int staticOffset, int[] offsets, out IntPtr final) =>
        TryWalk(ModuleBase + staticOffset, offsets, out final);

    /// <summary>
    /// Walk a pointer chain from <paramref name="anchor"/>: before each offset the
    /// current address is dereferenced, then the offset is added. The final offset
    /// is added but NOT dereferenced — the result is the address of the value.
    /// An empty <paramref name="offsets"/> returns the anchor unchanged.
    /// </summary>
    public bool TryWalk(IntPtr anchor, int[] offsets, out IntPtr final)
    {
        final = IntPtr.Zero;
        IntPtr addr = anchor;

        for (int i = 0; i < offsets.Length; i++)
        {
            if (!TryReadPointer(addr, out addr))
                return false;
            addr += offsets[i];
        }

        final = addr;
        return true;
    }

    private bool TryReadPointer(IntPtr address, out IntPtr pointer)
    {
        if (Is64Bit)
        {
            if (TryRead<long>(address, out var p)) { pointer = new IntPtr(p); return true; }
        }
        else
        {
            if (TryRead<int>(address, out var p)) { pointer = new IntPtr(p); return true; }
        }
        pointer = IntPtr.Zero;
        return false;
    }

    /// <summary>
    /// Scan the main module for a byte pattern. Use "??" for wildcard bytes,
    /// e.g. "8B 0D ?? ?? ?? ?? 89 41". Returns the absolute address of the
    /// first match, or IntPtr.Zero if none found. AOB scans survive game
    /// patches better than hardcoded pointer chains.
    /// </summary>
    public IntPtr AobScan(string pattern)
    {
        var all = AobScanAll(pattern, max: 1);
        return all.Count > 0 ? all[0] : IntPtr.Zero;
    }

    /// <summary>
    /// Return up to <paramref name="max"/> matches of a byte pattern in the main
    /// module, in ascending address order. Use this to check that a signature is
    /// unique (exactly one match) before trusting it.
    /// </summary>
    public List<IntPtr> AobScanAll(string pattern, int max = 64)
    {
        var (bytes, mask) = ParsePattern(pattern);
        int moduleSize = Process.MainModule!.ModuleMemorySize;
        var results = new List<IntPtr>();
        var seen = new HashSet<long>();

        // Read the whole module image in chunks to bound memory use. Chunks overlap
        // by (pattern length - 1) so a match straddling a boundary isn't missed; the
        // HashSet dedupes the few re-scanned bytes in the overlap region.
        const int chunkSize = 0x100000; // 1 MB
        int overlap = bytes.Length - 1;

        for (long offset = 0; offset < moduleSize; offset += chunkSize - overlap)
        {
            int toRead = (int)Math.Min(chunkSize, moduleSize - offset);
            var buffer = new byte[toRead];
            if (!ReadProcessMemory(_handle, ModuleBase + (int)offset, buffer, toRead, out var read) || (int)read < bytes.Length)
                continue;

            int from = 0;
            while ((from = FindPattern(buffer, (int)read, bytes, mask, from)) >= 0)
            {
                long abs = ModuleBase.ToInt64() + offset + from;
                if (seen.Add(abs))
                {
                    results.Add(new IntPtr(abs));
                    if (results.Count >= max)
                        return results;
                }
                from++;
            }
        }
        return results;
    }

    private static int FindPattern(byte[] haystack, int length, byte[] pattern, bool[] mask, int start = 0)
    {
        int last = length - pattern.Length;
        for (int i = start; i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (mask[j] && haystack[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static (byte[] bytes, bool[] mask) ParsePattern(string pattern)
    {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var mask = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "??" or "?")
            {
                mask[i] = false;
                bytes[i] = 0;
            }
            else
            {
                mask[i] = true;
                bytes[i] = Convert.ToByte(tokens[i], 16);
            }
        }
        return (bytes, mask);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            CloseHandle(_handle);
        Process.Dispose();
    }
}
