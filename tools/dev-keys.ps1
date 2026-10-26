<#
.SYNOPSIS
    Development helper: focuses the game window and sends real key presses (SendInput), for testing keyboard paths.

.EXAMPLE
    .\tools\dev-keys.ps1 Down Down Return Escape
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)][string[]]$Keys,
    [int]$DelayMs = 350
)

$ErrorActionPreference = "Stop"
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class DevKeys {
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit, Size = 40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    // Park the cursor over the 3D scene so mouse hover doesn't fight keyboard navigation.
    public static void ParkCursor(IntPtr hwnd) {
        RECT r; GetWindowRect(hwnd, out r);
        SetCursorPos(r.Left + (r.Right - r.Left) * 9 / 10, r.Top + (r.Bottom - r.Top) / 4);
    }
    public static void Press(ushort vk) {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        uint ext = (vk >= 0x21 && vk <= 0x2E) ? 1u : 0u;   // arrows, home/end, etc. are extended keys
        var down = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = 0x8 | ext } };
        var up = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = 0x8 | 0x2 | ext } };
        SendInput(1, new[] { down }, Marshal.SizeOf<INPUT>());
        System.Threading.Thread.Sleep(60);
        SendInput(1, new[] { up }, Marshal.SizeOf<INPUT>());
    }
    public static void Focus(IntPtr hwnd) {
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);   // Alt tap unlocks SetForegroundWindow
        ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
    }
}
"@

$proc = Get-Process MoonSweepers -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { throw "Moon Sweepers isn't running." }
[DevKeys]::Focus($proc.MainWindowHandle)
[DevKeys]::ParkCursor($proc.MainWindowHandle)
Start-Sleep -Milliseconds 400
if ([DevKeys]::GetForegroundWindow() -ne $proc.MainWindowHandle) { Write-Warning "Couldn't bring the game to the foreground." }

$vk = @{ Up = 0x26; Down = 0x28; Left = 0x25; Right = 0x27; Return = 0x0D; Enter = 0x0D; Escape = 0x1B; Esc = 0x1B; Space = 0x20; Tab = 0x09; W = 0x57; S = 0x53 }
foreach ($k in $Keys) {
    if (-not $vk.ContainsKey($k)) { throw "Unknown key '$k'." }
    [DevKeys]::Press([uint16]$vk[$k])
    Start-Sleep -Milliseconds $DelayMs
}
