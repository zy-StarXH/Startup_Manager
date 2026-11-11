# Native host smoke test: launches the app, exercises the native caption buttons,
# verifies WebView2 content, single-instance activation, and DPI-aware screenshots.
# No backend/UAC operations are performed.
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$OutDir,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $root 'src\StartupManager.App\bin\x64\Debug\net8.0-windows\win-x64\StartupManager.exe'
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $root '_qa'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class QaNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}
'@

function Get-Text([int[]]$codes) {
    return -join ($codes | ForEach-Object { [char]$_ })
}

$captionMinimize = Get-Text @(0x6700, 0x5C0F, 0x5316)
$captionMaximize = Get-Text @(0x6700, 0x5927, 0x5316)
$captionRestore = Get-Text @(0x5411, 0x4E0B, 0x8FD8, 0x539F)
$captionClose = Get-Text @(0x5173, 0x95ED)

# Must be set before touching any UI/Drawing API so CopyFromScreen uses physical pixels.
[QaNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Get-WindowRect([IntPtr]$handle) {
    $rect = New-Object QaNative+RECT
    if (-not [QaNative]::GetWindowRect($handle, [ref]$rect)) {
        throw 'GetWindowRect failed.'
    }
    return $rect
}

function Get-WorkArea([IntPtr]$handle) {
    $monitor = [QaNative]::MonitorFromWindow($handle, 2)
    $info = New-Object QaNative+MONITORINFO
    $info.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][QaNative+MONITORINFO])
    [QaNative]::GetMonitorInfo($monitor, [ref]$info) | Out-Null
    return $info
}

function Save-WindowShot([IntPtr]$handle, [string]$name, [switch]$TitlebarOnly) {
    $rect = Get-WindowRect $handle
    $width = $rect.Right - $rect.Left
    $fullHeight = $rect.Bottom - $rect.Top
    $dpi = [QaNative]::GetDpiForWindow($handle)
    if ($dpi -eq 0) { $dpi = 96 }
    $cropHeight = $fullHeight
    if ($TitlebarOnly) {
        $cropHeight = [int][Math]::Round(46 * $dpi / 96.0)
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $fullHeight)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        # PrintWindow captures the WPF + WebView2 surface without desktop overlays
        # (taskbar, notification balloons). Fall back to a screen copy if it fails.
        $hdc = $graphics.GetHdc()
        try {
            $printed = [QaNative]::PrintWindow($handle, $hdc, 2)
        }
        finally {
            $graphics.ReleaseHdc($hdc)
        }
        if (-not $printed) {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($width, $fullHeight)))
        }

        if ($cropHeight -lt $fullHeight) {
            $cropRect = New-Object System.Drawing.Rectangle(0, 0, $width, $cropHeight)
            $cropped = $bitmap.Clone($cropRect, $bitmap.PixelFormat)
            try {
                $path = Join-Path $OutDir $name
                $cropped.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
                Write-Host ("screenshot: {0} ({1}x{2})" -f $path, $width, $cropHeight)
                return $path
            }
            finally {
                $cropped.Dispose()
            }
        }

        $path = Join-Path $OutDir $name
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("screenshot: {0} ({1}x{2})" -f $path, $width, $fullHeight)
        return $path
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-WindowElement([int]$processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $condition)
}

function Find-ByAutomationId($windowElement, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $windowElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-NativeButton($windowElement, [string]$automationId, [string]$expectedName) {
    $button = Find-ByAutomationId $windowElement $automationId
    if ($null -eq $button) {
        throw "Native caption button not found: $automationId"
    }
    if ($button.Current.Name -ne $expectedName) {
        throw "Caption button $automationId has unexpected name '$($button.Current.Name)'."
    }
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Read-NewLog([string]$path, [long]$offset) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [System.IO.File]::Open($path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Position = [Math]::Min($offset, $stream.Length)
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Invoke-Drag([int]$fromX, [int]$fromY, [int]$toX, [int]$toY) {
    [QaNative]::SetCursorPos($fromX, $fromY) | Out-Null
    Start-Sleep -Milliseconds 150
    [QaNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
    for ($step = 1; $step -le 12; $step++) {
        $x = [int]($fromX + (($toX - $fromX) * $step / 12))
        $y = [int]($fromY + (($toY - $fromY) * $step / 12))
        [QaNative]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 16
    }
    Start-Sleep -Milliseconds 100
    [QaNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
}

function Invoke-DoubleClick([int]$x, [int]$y) {
    [QaNative]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [QaNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [QaNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [QaNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [QaNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 1000
}

function Wait-For([scriptblock]$condition, [int]$timeoutMs, [string]$description) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for $description."
}

# Deterministic placement + no stale instance.
Get-Process StartupManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$placementFile = Join-Path $env:LOCALAPPDATA 'StartupManager\window.json'
Remove-Item -LiteralPath $placementFile -ErrorAction SilentlyContinue

$logFile = Join-Path $env:LOCALAPPDATA ('StartupManager\Logs\app-' + (Get-Date).ToString('yyyyMMdd') + '.log')
$logOffset = if (Test-Path -LiteralPath $logFile) { (Get-Item -LiteralPath $logFile).Length } else { 0 }

$process = Start-Process -FilePath $ExePath -PassThru
try {
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 } 20000 'the main window' | Out-Null
    $handle = $process.MainWindowHandle
    Write-Host ("launched: pid={0} hwnd={1}" -f $process.Id, $handle)

    # Let WebView2 load the web assets, then activate the window.
    Start-Sleep -Seconds 5
    $shell = New-Object -ComObject WScript.Shell
    $shell.AppActivate($process.Id) | Out-Null
    [QaNative]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 800

    # Move the cursor away from the auto-hidden taskbar before capturing.
    [QaNative]::SetCursorPos(1440, 700) | Out-Null
    Start-Sleep -Milliseconds 900

    $windowElement = Get-WindowElement $process.Id
    if ($null -eq $windowElement) {
        throw 'UI Automation could not find the main window.'
    }

    $webLoaded = (Read-NewLog $logFile $logOffset).Contains('WebView2 navigation completed successfully')
    if (-not $webLoaded) {
        Write-Warning 'The log does not show a successful WebView2 navigation; check the screenshot.'
    }

    $initialRect = Get-WindowRect $handle
    $workArea = Get-WorkArea $handle
    $dpi = [QaNative]::GetDpiForWindow($handle)
    Write-Host ("window: {0}x{1} at {2},{3}; dpi={4}; workarea={5}x{6}" -f
        ($initialRect.Right - $initialRect.Left), ($initialRect.Bottom - $initialRect.Top),
        $initialRect.Left, $initialRect.Top, $dpi,
        ($workArea.rcWork.Right - $workArea.rcWork.Left), ($workArea.rcWork.Bottom - $workArea.rcWork.Top))
    Write-Host ("web content accessible: {0}" -f $webLoaded)
    Save-WindowShot $handle 'native-host.png' | Out-Null
    Save-WindowShot $handle 'titlebar.png' -TitlebarOnly | Out-Null

    # Windows 11 rounded corners are applied through DWM.
    $cornerPreference = 0
    $dwmResult = [QaNative]::DwmGetWindowAttribute($handle, 33, [ref]$cornerPreference, 4)
    if ($dwmResult -eq 0 -and $cornerPreference -ne 2) {
        throw "DWM corner preference is $cornerPreference, expected 2 (round)."
    }
    Write-Host ("dwm rounded corners: OK (preference={0})" -f $cornerPreference)

    $midX = [int](($initialRect.Left + $initialRect.Right) / 2)
    $titleY = $initialRect.Top + 70

    # Caption drag moves the window.
    Invoke-Drag $midX $titleY ($midX + 160) ($titleY + 80)
    $draggedRect = Get-WindowRect $handle
    if ($draggedRect.Left -lt ($initialRect.Left + 100) -or $draggedRect.Top -lt ($initialRect.Top + 40)) {
        throw ("Caption drag did not move the window: {0},{1}." -f $draggedRect.Left, $draggedRect.Top)
    }
    Write-Host 'caption drag: OK'
    Invoke-Drag ([int](($initialRect.Left + $initialRect.Right) / 2 + 160)) ([int]($titleY + 80)) $midX $titleY

    # Resize by dragging the right edge.
    $beforeResize = Get-WindowRect $handle
    $rightY = [int](($beforeResize.Top + $beforeResize.Bottom) / 2)
    Invoke-Drag -fromX ($beforeResize.Right - 8) -fromY $rightY -toX ($beforeResize.Right - 168) -toY $rightY
    $afterResize = Get-WindowRect $handle
    if (($beforeResize.Right - $beforeResize.Left) - ($afterResize.Right - $afterResize.Left) -lt 100) {
        throw 'Dragging the right window edge did not resize the window.'
    }
    Write-Host 'resize border: OK'
    Invoke-Drag ($afterResize.Right - 8) $rightY ($afterResize.Right + 160) $rightY

    # Double-click the caption toggles maximize/restore.
    Invoke-DoubleClick $midX $titleY
    $doubleClickMax = Get-WindowRect $handle
    $workWidth0 = $workArea.rcWork.Right - $workArea.rcWork.Left
    if (($doubleClickMax.Right - $doubleClickMax.Left) -ne $workWidth0) {
        throw 'Double-click on the titlebar did not maximize the window.'
    }
    Invoke-DoubleClick $midX 40
    $doubleClickRestore = Get-WindowRect $handle
    if (($doubleClickRestore.Right - $doubleClickRestore.Left) -eq $workWidth0) {
        throw 'Double-click did not restore the window.'
    }
    Write-Host 'double-click maximize/restore: OK'
    $baselineRect = Get-WindowRect $handle

    # Close-button hover state (turns red).
    $closeButton = Find-ByAutomationId $windowElement 'CaptionClose'
    if ($null -ne $closeButton) {
        if ($closeButton.Current.Name -ne $captionClose) { throw 'Close button has an unexpected accessible name.' }
        $bounds = $closeButton.Current.BoundingRectangle
        [QaNative]::SetCursorPos([int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2)) | Out-Null
        Start-Sleep -Milliseconds 500
        Save-WindowShot $handle 'titlebar-close-hover.png' -TitlebarOnly | Out-Null
        [QaNative]::SetCursorPos(1440, 700) | Out-Null
        Start-Sleep -Milliseconds 300
    }

    # Minimize.
    Invoke-NativeButton $windowElement 'CaptionMinimize' $captionMinimize
    Start-Sleep -Milliseconds 900
    if (-not [QaNative]::IsIconic($handle)) { throw 'Minimize button did not minimize the window.' }
    Write-Host 'minimize: OK'

    # Restore from the taskbar (button glyph must be back to maximize).
    [QaNative]::ShowWindow($handle, 9) | Out-Null
    Start-Sleep -Milliseconds 900
    if ([QaNative]::IsIconic($handle)) { throw 'Window did not restore.' }
    $maxButton = Find-ByAutomationId $windowElement 'CaptionMaximize'
    if ($null -eq $maxButton -or $maxButton.Current.Name -ne $captionMaximize) {
        throw 'Maximize button name did not return to its normal state.'
    }
    Write-Host 'restore: OK'

    # Maximize.
    Invoke-NativeButton $windowElement 'CaptionMaximize' $captionMaximize
    Start-Sleep -Milliseconds 1200
    $maximizedRect = Get-WindowRect $handle
    $maximizedButton = Find-ByAutomationId $windowElement 'CaptionMaximize'
    if ($null -eq $maximizedButton -or $maximizedButton.Current.Name -ne $captionRestore) {
        throw 'Maximize glyph/tooltip did not update to the restore state.'
    }
    $workWidth = $workArea.rcWork.Right - $workArea.rcWork.Left
    $workHeight = $workArea.rcWork.Bottom - $workArea.rcWork.Top
    if ((($maximizedRect.Right - $maximizedRect.Left) -ne $workWidth) -or
        (($maximizedRect.Bottom - $maximizedRect.Top) -ne $workHeight)) {
        throw ("Maximized window {0}x{1} does not match work area {2}x{3}." -f
            ($maximizedRect.Right - $maximizedRect.Left), ($maximizedRect.Bottom - $maximizedRect.Top), $workWidth, $workHeight)
    }
    Write-Host ("maximize: OK ({0}x{1})" -f $workWidth, $workHeight)
    Save-WindowShot $handle 'native-host-maximized.png' | Out-Null

    # Restore.
    Invoke-NativeButton $windowElement 'CaptionMaximize' $captionRestore
    Start-Sleep -Milliseconds 1200
    $restoredRect = Get-WindowRect $handle
    if (($restoredRect.Right - $restoredRect.Left) -ne ($baselineRect.Right - $baselineRect.Left)) {
        throw 'Window did not return to its restored size.'
    }
    Write-Host 'restore from maximized: OK'

    # Close hides to tray (minimizeToTray defaults to true with no backend).
    Invoke-NativeButton $windowElement 'CaptionClose' $captionClose
    Start-Sleep -Milliseconds 1200
    $process.Refresh()
    if ($process.HasExited) { throw 'Close exited the process instead of minimizing to tray.' }
    if ([QaNative]::IsWindowVisible($handle)) { throw 'Close did not hide the window.' }
    Write-Host 'close to tray: OK'

    # A second launch must activate the hidden first instance and exit.
    $second = Start-Process -FilePath $ExePath -PassThru
    Start-Sleep -Seconds 2
    $second.Refresh()
    $firstVisible = [QaNative]::IsWindowVisible($handle)
    if (-not $second.HasExited) {
        Stop-Process -Id $second.Id -Force
        throw 'Second instance did not exit.'
    }
    if (-not $firstVisible) { throw 'Second launch did not restore the first window.' }
    Write-Host 'single instance + activation: OK'

    Save-WindowShot $handle 'native-host-reactivated.png' | Out-Null
    Write-Host 'QA PASSED'
}
finally {
    if (-not $KeepRunning) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
            }
        }
        catch {
            # best effort
        }
    }
}
