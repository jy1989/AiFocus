# Smoke test of the existing AI Focus's WM_HOTKEY show/hide path.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FocusWin32Test {
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    public static IntPtr FindByProcess(uint target) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((hwnd, param) => { uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == target) { result = hwnd; return false; } return true; }, IntPtr.Zero);
        return result;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);
}
'@
$process = Get-Process AiFocus -ErrorAction Stop | Select-Object -First 1
$handle = [FocusWin32Test]::FindByProcess([uint32]$process.Id)
if ($handle -eq [IntPtr]::Zero) { throw 'AI Focus window handle not found' }
$hotkeyMessage = 0x0312
$hotkeyId = [IntPtr]0xA1C0
if (-not [FocusWin32Test]::PostMessage($handle, $hotkeyMessage, $hotkeyId, [IntPtr]::Zero)) {
    throw 'Unable to post hotkey message'
}
Start-Sleep -Milliseconds 1400
$visible = [FocusWin32Test]::IsWindowVisible($handle)
Write-Output ('PANEL_VISIBLE_AFTER_HOTKEY=' + $visible)
# Close the panel regardless of whether the show assertion passes.
[FocusWin32Test]::PostMessage($handle, $hotkeyMessage, $hotkeyId, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 500
Write-Output ('PANEL_HIDDEN_AFTER_SECOND_HOTKEY=' + (-not [FocusWin32Test]::IsWindowVisible($handle)))
if (-not $visible) { exit 1 }
