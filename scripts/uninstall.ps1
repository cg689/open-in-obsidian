<#
.SYNOPSIS
  Undo everything install.ps1 did.

.DESCRIPTION
  Removes the "Obsidian.md" ProgId, drops it from the "Open with" list, clears
  the .md default (per-user only), unregisters the bridge vault from Obsidian's
  vault list and deletes the bridge vault folder. Explorer is notified
  immediately. After running this, pick whichever editor you like via
  right-click -> Open with -> Choose another app.

  The bridge vault contains directory junctions pointing at folders elsewhere on
  disk, so it is dismantled carefully: every reparse point is removed with
  non-recursive semantics first. A recursive delete would follow the junctions
  and wipe the real folders they point at.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\OpenInObsidian"
)

$ErrorActionPreference = "Continue"

$bridgeVault = Join-Path $InstallDir "vault"

# ---------------------------------------------------------------------------
# Removes just the bridge vault entry from Obsidian's vault list, textually, so
# the rest of the file (including the user's own vaults) is left byte-for-byte
# as Obsidian wrote it. The result is re-parsed before being written back.
# ---------------------------------------------------------------------------
function Unregister-BridgeVault
{
    param([string]$VaultPath)

    $cfg = Join-Path $env:APPDATA "obsidian\obsidian.json"
    if (-not (Test-Path -LiteralPath $cfg)) { return "obsidian.json not found - nothing to unregister." }

    $raw = [System.IO.File]::ReadAllText($cfg)
    $parsed = $null
    try { $parsed = $raw | ConvertFrom-Json }
    catch { return "obsidian.json is not valid JSON - left untouched." }
    if (-not $parsed.vaults) { return $null }

    $wanted = $VaultPath.TrimEnd('\')
    $id = $null
    foreach ($prop in $parsed.vaults.PSObject.Properties)
    {
        $vp = $prop.Value.path
        if ($vp -and $vp.TrimEnd('\') -ieq $wanted) { $id = $prop.Name; break }
    }
    if (-not $id) { return $null }

    if (Get-Process -Name Obsidian -ErrorAction SilentlyContinue)
    {
        return "Obsidian is running. Close it completely and re-run uninstall to clean the vault list."
    }

    $at = $raw.IndexOf('"' + $id + '":')
    if ($at -lt 0) { return "could not locate the bridge vault entry - left untouched." }

    # Walk the entry's object literal to find where it ends.
    $start = $raw.IndexOf('{', $at)
    $depth = 0
    $end = -1
    for ($i = $start; $i -lt $raw.Length; $i++)
    {
        if ($raw[$i] -eq '{') { $depth++ }
        elseif ($raw[$i] -eq '}')
        {
            $depth--
            if ($depth -eq 0) { $end = $i; break }
        }
    }
    if ($end -lt 0) { return "could not parse the bridge vault entry - left untouched." }

    $before = $raw.Substring(0, $at)
    $after = $raw.Substring($end + 1)

    # Drop the comma that used to separate this entry from its neighbour.
    if ($after.StartsWith(",")) { $after = $after.Substring(1) }
    elseif ($before.EndsWith(",")) { $before = $before.Substring(0, $before.Length - 1) }

    $updated = $before + $after
    try { $updated | ConvertFrom-Json | Out-Null }
    catch { return "refusing to write: the updated obsidian.json would not be valid JSON." }

    [System.IO.File]::WriteAllText($cfg, $updated, (New-Object System.Text.UTF8Encoding($false)))
    return $null
}

Write-Host "== Open in Obsidian - uninstall ==" -ForegroundColor Cyan

# 1. Remove the ProgId.
$progId = "HKCU:\Software\Classes\Obsidian.md"
if (Test-Path $progId)
{
    Remove-Item -Path $progId -Recurse -Force
    Write-Host "[1/5] Removed ProgId: Obsidian.md"
}
else
{
    Write-Host "[1/5] ProgId not present (nothing to do)."
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
            Write-Host "[2/5] Restored previous .md default: $restore"
        }
        else
        {
            Remove-ItemProperty -Path $mdKey -Name "(default)" -ErrorAction SilentlyContinue
            Write-Host "[2/5] Cleared .md default."
        }
    }
    else
    {
        Write-Host "[2/5] .md default is '$cur' - left untouched."
    }
}

# 3. Unregister the bridge vault from Obsidian's vault list.
$vaultNote = Unregister-BridgeVault -VaultPath $bridgeVault
if ($vaultNote)
{
    Write-Host "[3/5] Bridge vault   : $vaultNote" -ForegroundColor Yellow
}
else
{
    Write-Host "[3/5] Bridge vault   : unregistered from Obsidian"
}

# 4. Delete the bridge vault, junctions first and never recursively through them.
$vaultRemoved = $true
if (Test-Path -LiteralPath $bridgeVault)
{
    Add-Type -Namespace Win32 -Name NativeDir -MemberDefinition @'
[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool RemoveDirectory(string pathName);
'@

    foreach ($child in @(Get-ChildItem -LiteralPath $bridgeVault -Directory -Force -ErrorAction SilentlyContinue))
    {
        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            # Removes the junction itself. Never descends into its target.
            [void][Win32.NativeDir]::RemoveDirectory($child.FullName)
        }
    }

    $leftover = @(Get-ChildItem -LiteralPath $bridgeVault -Directory -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })

    if ($leftover.Count -gt 0)
    {
        $vaultRemoved = $false
        Write-Host "[4/5] Bridge vault   : SKIPPED - $($leftover.Count) junction(s) could not be removed" -ForegroundColor Yellow
        Write-Host "      Refusing to delete $bridgeVault recursively while junctions remain." -ForegroundColor Yellow
        Write-Host "      Remove them by hand (rd /q `"<path>`") and delete the folder." -ForegroundColor Yellow
    }
    else
    {
        Remove-Item -LiteralPath $bridgeVault -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "[4/5] Bridge vault   : deleted"
    }
}
else
{
    Write-Host "[4/5] Bridge vault   : not present (nothing to do)."
}

# 5. Notify Explorer.
try
{
    Add-Type -Namespace Win32 -Name ShellNotify -MemberDefinition @'
[DllImport("shell32.dll")]
public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
'@
    [Win32.ShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    Write-Host "[5/5] Explorer notified."
}
catch { }

# Optional: delete the helper exe directory.
if ((Test-Path $InstallDir) -and $vaultRemoved)
{
    $answer = Read-Host "Delete helper exe directory '$InstallDir'? [y/N]"
    if ($answer -match '^[Yy]') { Remove-Item -Path $InstallDir -Recurse -Force; Write-Host "Removed $InstallDir" }
}

Write-Host ""
Write-Host "Uninstalled. Set your preferred .md editor via right-click -> Open with." -ForegroundColor Green
