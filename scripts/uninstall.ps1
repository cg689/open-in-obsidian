<#
.SYNOPSIS
  Undo everything install.ps1 did.

.DESCRIPTION
  Removes the "Obsidian.md" ProgId, drops it from the "Open with" list and
  clears the .md default (per-user only). Explorer is notified immediately.
  After running this, pick whichever editor you like via right-click ->
  Open with -> Choose another app.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\OpenInObsidian"
)

$ErrorActionPreference = "Continue"

Write-Host "== Open in Obsidian - uninstall ==" -ForegroundColor Cyan

# 1. Remove the ProgId.
$progId = "HKCU:\Software\Classes\Obsidian.md"
if (Test-Path $progId)
{
    Remove-Item -Path $progId -Recurse -Force
    Write-Host "[1/3] Removed ProgId: Obsidian.md"
}
else
{
    Write-Host "[1/3] ProgId not present (nothing to do)."
}

# 2. Drop it from the "Open with" list and clear the .md default.
$openWith = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\OpenWithProgids"
if (Test-Path $openWith)
{
    Remove-ItemProperty -Path $openWith -Name "Obsidian.md" -ErrorAction SilentlyContinue
}
$mdKey = "HKCU:\Software\Classes\.md"
if (Test-Path $mdKey)
{
    $cur = ""
    try { $cur = (Get-ItemProperty -Path $mdKey)."(default)" } catch { }
    if ($cur -eq "Obsidian.md")
    {
        # Restore backed-up default if present, otherwise clear it.
        $backupFile = Join-Path $InstallDir "previous-md-default.txt"
        $restore = ""
        if (Test-Path $backupFile)
        {
            $line = (Get-Content $backupFile -ErrorAction SilentlyContinue | Select-Object -First 1)
            if ($line -match '= (\S.*)$') { $restore = $Matches[1].Trim() }
        }
        if ($restore -and $restore -ne "Obsidian.md")
        {
            # A backup containing Obsidian.md itself is a relic of an old
            # reinstall (pre-fix installers clobbered the backup) - treat it
            # as no backup rather than "restoring" our own ProgId.
            Set-ItemProperty -Path $mdKey -Name "(default)" -Value $restore
            Write-Host "[2/3] Restored previous .md default: $restore"
        }
        else
        {
            Remove-ItemProperty -Path $mdKey -Name "(default)" -ErrorAction SilentlyContinue
            Write-Host "[2/3] Cleared .md default."
        }
    }
    else
    {
        Write-Host "[2/3] .md default is '$cur' - left untouched."
    }
}

# 3. Notify Explorer.
try
{
    Add-Type -Namespace Win32 -Name ShellNotify -MemberDefinition @'
[DllImport("shell32.dll")]
public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
'@
    [Win32.ShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    Write-Host "[3/3] Explorer notified."
}
catch { }

# Optional: delete the helper exe directory.
if (Test-Path $InstallDir)
{
    $answer = Read-Host "Delete helper exe directory '$InstallDir'? [y/N]"
    if ($answer -match '^[Yy]') { Remove-Item -Path $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir" }
}

Write-Host ""
Write-Host "Uninstalled. Set your preferred .md editor via right-click -> Open with." -ForegroundColor Green
