# Helper for driving the ReviewScope window: screenshot, click, drag, keys.
param(
    [Parameter(Mandatory = $true)][string]$Action,
    [string]$OutFile,
    [int]$X, [int]$Y, [int]$X2, [int]$Y2,
    [string]$Keys
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint LEFTDOWN = 0x02, LEFTUP = 0x04;
}
"@

$proc = Get-Process ReviewScope.App -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($null -eq $proc) { throw "ReviewScope.App window not found" }
$hwnd = $proc.MainWindowHandle
[Win32]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 250

$rect = New-Object Win32+RECT
[Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null

switch ($Action) {
    "screenshot" {
        $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output "saved $OutFile ($w x $h, window at $($rect.Left),$($rect.Top))"
    }
    "click" {
        [Win32]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
        Start-Sleep -Milliseconds 120
        [Win32]::mouse_event([Win32]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [Win32]::mouse_event([Win32]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        Write-Output "clicked $X,$Y"
    }
    "drag" {
        # Window-relative drag from (X,Y) to (X2,Y2) with intermediate moves (freedraw needs samples).
        [Win32]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        [Win32]::mouse_event([Win32]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        $steps = 30
        for ($i = 1; $i -le $steps; $i++) {
            $t = $i / $steps
            # slight sine wobble so a freedraw stroke looks like handwriting
            $wob = [int](14 * [Math]::Sin($t * 6.28 * 1.5))
            $px = [int]($rect.Left + $X + ($X2 - $X) * $t)
            $py = [int]($rect.Top + $Y + ($Y2 - $Y) * $t + $wob)
            [Win32]::SetCursorPos($px, $py) | Out-Null
            Start-Sleep -Milliseconds 18
        }
        [Win32]::mouse_event([Win32]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        Write-Output "dragged $X,$Y -> $X2,$Y2"
    }
    "dragline" {
        # Straight drag, no wobble (for shapes / lines).
        [Win32]::SetCursorPos($rect.Left + $X, $rect.Top + $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        [Win32]::mouse_event([Win32]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        $steps = 16
        for ($i = 1; $i -le $steps; $i++) {
            $t = $i / $steps
            $px = [int]($rect.Left + $X + ($X2 - $X) * $t)
            $py = [int]($rect.Top + $Y + ($Y2 - $Y) * $t)
            [Win32]::SetCursorPos($px, $py) | Out-Null
            Start-Sleep -Milliseconds 15
        }
        [Win32]::mouse_event([Win32]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        Write-Output "dragged line $X,$Y -> $X2,$Y2"
    }
    "keys" {
        $shell = New-Object -ComObject WScript.Shell
        $shell.SendKeys($Keys)
        Write-Output "sent keys: $Keys"
    }
    default { throw "unknown action $Action" }
}
