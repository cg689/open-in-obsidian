<#
.SYNOPSIS
  Install "Open in Obsidian": double-click a .md file to open exactly that
  file in Obsidian, silently (no console window, no popup).

.DESCRIPTION
  What it does, step by step:

    1. Locates Obsidian.exe (URI handler registry, common install paths,
       or the -ObsidianPath parameter).
    2. Compiles src\OpenInObsidian.cs into a tiny windowless GUI exe using
       the .NET Framework compiler (csc.exe) that ships with every Windows.
       No downloads, no dependencies, nothing prebuilt - you compile the
       source yourself, so you know exactly what runs.
    3. Creates the "bridge" vault next to the exe. Files outside every vault
       cannot be opened by obsidian://open?path= (the protocol only searches
       registered vaults), so the helper mounts their folders into this vault
       as directory junctions. See README for the details.
    4. Registers that bridge vault in Obsidian's own vault list
       (%APPDATA%\obsidian\obsidian.json), backing the file up first.
    5. Registers a file association ProgId "Obsidian.md" pointing at the exe.
    6. Makes "Obsidian.md" the default for the .md extension (current-user).
    7. Notifies Explorer so the change takes effect immediately
       (no reboot / re-login required).

  All changes are per-user (HKCU + %APPDATA%). No admin rights required.

.PARAMETER ObsidianPath
  Full path to Obsidian.exe. Auto-detected if omitted.

.PARAMETER InstallDir
  Where to place OpenInObsidian.exe and its bridge vault.
  Default: %LOCALAPPDATA%\OpenInObsidian

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -ObsidianPath "C:\Apps\Obsidian\Obsidian.exe"
#>
param(
    [string]$ObsidianPath = "",
    [string]$InstallDir = "$env:LOCALAPPDATA\OpenInObsidian"
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# 1. Locate Obsidian.exe
# ---------------------------------------------------------------------------
function Get-ObsidianPath
{
    param([string]$Explicit)

    if ($Explicit)
    {
        if (Test-Path -LiteralPath $Explicit) { return (Resolve-Path -LiteralPath $Explicit).Path }
        throw "ObsidianPath '$Explicit' does not exist."
    }

    # Preferred: the obsidian:// URI handler Obsidian registers itself.
    foreach ($key in @(
        "Registry::HKEY_CLASSES_ROOT\obsidian\shell\open\command",
        "HKCU:\Software\Classes\obsidian\shell\open\command"
    ))
    {
        try
        {
            $cmd = (Get-ItemProperty -Path $key -ErrorAction Stop)."(default)"
            if ($cmd -match '"([^"]+)"' -and (Test-Path -LiteralPath $Matches[1])) { return $Matches[1] }
            if ($cmd -match '^(\S+\.exe)' -and (Test-Path -LiteralPath $Matches[1])) { return $Matches[1] }
        }
        catch { }
    }

    # Fallback: common install locations.
    foreach ($p in @(
        "$env:LOCALAPPDATA\Programs\Obsidian\Obsidian.exe",
        "$env:LOCALAPPDATA\Obsidian\Obsidian.exe",
        "$env:ProgramFiles\Obsidian\Obsidian.exe",
        "${env:ProgramFiles(x86)}\Obsidian\Obsidian.exe"
    ))
    {
        if (Test-Path -LiteralPath $p) { return $p }
    }

    return $null
}

# ---------------------------------------------------------------------------
# 4. Register the bridge vault in Obsidian's vault list
#
# The entry is spliced in textually right after `"vaults":{` so that every other
# byte of obsidian.json - the user's own vaults included - stays exactly as
# Obsidian wrote it. The result is parsed again before anything is written back,
# so a malformed edit can never reach disk.
# ---------------------------------------------------------------------------
function Register-BridgeVault
{
    param([string]$VaultPath, [string]$BackupPath)

    $cfg = Join-Path $env:APPDATA "obsidian\obsidian.json"
    if (-not (Test-Path -LiteralPath $cfg))
    {
        return "obsidian.json not found. Open Obsidian once, then re-run install."
    }

    $raw = [System.IO.File]::ReadAllText($cfg)
    $parsed = $null
    try { $parsed = $raw | ConvertFrom-Json }
    catch { return "obsidian.json is not valid JSON - left untouched." }

    # Already registered (typical on a re-install)? Nothing to do.
    if ($parsed.vaults)
    {
        foreach ($prop in $parsed.vaults.PSObject.Properties)
        {
            $vp = $prop.Value.path
            if ($vp -and $vp.TrimEnd('\') -ieq $VaultPath.TrimEnd('\')) { return $null }
        }
    }

    # Obsidian rewrites this file from its in-memory vault list, so an edit made
    # while it is running would be silently discarded later.
    if (Get-Process -Name Obsidian -ErrorAction SilentlyContinue)
    {
        return "Obsidian is running. Close it completely and re-run install so the bridge vault can be registered."
    }

    if (-not (Test-Path -LiteralPath $BackupPath))
    {
        Copy-Item -LiteralPath $cfg -Destination $BackupPath
    }

    $anchor = '"vaults":{'
    $at = $raw.IndexOf($anchor)
    if ($at -lt 0) { return "obsidian.json has no 'vaults' object - left untouched." }
    $at += $anchor.Length

    $rest = $raw.Substring($at)
    $separator = ""
    if (-not $rest.StartsWith("}")) { $separator = "," }

    $id = ([Guid]::NewGuid().ToString("N")).Substring(0, 16)
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $entry = '"{0}":{{"path":"{1}","ts":{2}}}' -f $id, $VaultPath.Replace('\', '\\'), $ts

    $updated = $raw.Substring(0, $at) + $entry + $separator + $rest

    try { $updated | ConvertFrom-Json | Out-Null }
    catch { return "refusing to write: the updated obsidian.json would not be valid JSON." }

    [System.IO.File]::WriteAllText($cfg, $updated, (New-Object System.Text.UTF8Encoding($false)))
    return $null
}

Write-Host "== Open in Obsidian - install ==" -ForegroundColor Cyan

$obsidian = Get-ObsidianPath -Explicit $ObsidianPath
if (-not $obsidian)
{
    throw "Obsidian.exe not found. Run again with: -ObsidianPath 'C:\path\to\Obsidian.exe'"
}
Write-Host "[1/7] Obsidian found : $obsidian"

# ---------------------------------------------------------------------------
# 2. Compile the helper exe from source (nothing prebuilt, fully transparent)
# ---------------------------------------------------------------------------
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc))
{
    $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path $csc)) { throw "csc.exe not found. .NET Framework 3.5/4.x is required." }

$source = Join-Path $PSScriptRoot "..\src\OpenInObsidian.cs"
if (-not (Test-Path $source)) { throw "Source file not found: $source" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$helperExe = Join-Path $InstallDir "OpenInObsidian.exe"

& $csc /nologo /target:winexe /optimize+ /r:System.Web.Extensions.dll /out:"$helperExe" "$source"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $helperExe))
{
    throw "Compilation failed (csc exit code $LASTEXITCODE)."
}
Write-Host "[2/7] Helper compiled : $helperExe"

# ---------------------------------------------------------------------------
# 3. Create the bridge vault
# ---------------------------------------------------------------------------
$bridgeVault = Join-Path $InstallDir "vault"
New-Item -ItemType Directory -Force -Path (Join-Path $bridgeVault ".obsidian") | Out-Null
Write-Host "[3/7] Bridge vault   : $bridgeVault"

# ---------------------------------------------------------------------------
# 4. Register the bridge vault with Obsidian
# ---------------------------------------------------------------------------
$vaultBackup = Join-Path $InstallDir "obsidian.json.bak"
$vaultNote = Register-BridgeVault -VaultPath $bridgeVault -BackupPath $vaultBackup
if ($vaultNote)
{
    Write-Host "[4/7] Bridge vault   : NOT registered" -ForegroundColor Yellow
}
else
{
    Write-Host "[4/7] Bridge vault   : registered with Obsidian"
}

# ---------------------------------------------------------------------------
# 5. Register the "Obsidian.md" ProgId (per-user)
# ---------------------------------------------------------------------------
$progId = "HKCU:\Software\Classes\Obsidian.md"
# Backup the current default for .md before touching anything.
# Never overwrite an existing backup: on a reinstall the "current" default is
# our own Obsidian.md, and clobbering the backup would lose the user's true
# previous default (uninstall would then "restore" Obsidian.md).
$backupFile = Join-Path $InstallDir "previous-md-default.txt"
try
{
    $old = (Get-ItemProperty -Path "HKCU:\Software\Classes\.md" -ErrorAction Stop)."(default)"
    if (-not (Test-Path $backupFile) -and $old -ne "Obsidian.md")
    {
        "HKCU\Software\Classes\.md = $old" | Out-File -FilePath $backupFile -Encoding ASCII
    }
}
catch { }

New-Item -Path "$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$progId" -Name "(default)" -Value "Markdown File (Obsidian)"
New-Item -Path "$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$progId\DefaultIcon" -Name "(default)" -Value ('"{0}",0' -f $obsidian)
Set-ItemProperty -Path "$progId\shell\open\command" -Name "(default)" -Value ('"{0}" "%1"' -f $helperExe)
Write-Host "[5/7] ProgId registered: Obsidian.md -> $helperExe"

# ---------------------------------------------------------------------------
# 6. Make it the default for .md (per-user)
# ---------------------------------------------------------------------------
New-Item -Path "HKCU:\Software\Classes\.md" -Force | Out-Null
Set-ItemProperty -Path "HKCU:\Software\Classes\.md" -Name "(default)" -Value "Obsidian.md"

# Expose it in the "Open with" list as well.
$openWith = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\OpenWithProgids"
New-Item -Path $openWith -Force | Out-Null
Set-ItemProperty -Path $openWith -Name "Obsidian.md" -Value ([byte[]]@()) -Type Binary

# A stale UserChoice (left by another app, or a broken hash) takes priority
# over our default. Try to clear it; the key is ACL-protected, so this may
# legitimately fail - we then tell the user how to fix it manually.
$userChoiceNote = ""
$uc = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.md\UserChoice"
if (Test-Path $uc)
{
    $ucProgId = ""
    try { $ucProgId = (Get-ItemProperty -Path $uc -ErrorAction Stop).ProgId } catch { }
    if ($ucProgId -ne "Obsidian.md")
    {
        try
        {
            Remove-Item -Path $uc -Recurse -Force -ErrorAction Stop
            $userChoiceNote = "Cleared a stale UserChoice ($ucProgId)."
        }
        catch
        {
            $userChoiceNote = "Windows keeps a protected UserChoice for .md pointing to '$ucProgId'. " +
                "If double-click still opens another app, right-click any .md -> Open with -> Choose another app -> " +
                "pick 'Markdown File (Obsidian)' and tick 'Always use this app'."
        }
    }
}
Write-Host "[6/7] .md default set  : Obsidian.md"

# ---------------------------------------------------------------------------
# 7. Tell Explorer the association changed (no reboot needed)
# ---------------------------------------------------------------------------
try
{
    Add-Type -Namespace Win32 -Name ShellNotify -MemberDefinition @'
[DllImport("shell32.dll")]
public static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
'@
    # SHCNE_ASSOCCHANGED
    [Win32.ShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
    Write-Host "[7/7] Explorer notified: change takes effect immediately."
}
catch
{
    Write-Host "[7/7] Could not notify Explorer. Log off / log on (or reboot) to apply."
}

Write-Host ""
Write-Host "Done. Try double-clicking a .md file inside one of your Obsidian vaults." -ForegroundColor Green
if ($userChoiceNote) { Write-Host "Note: $userChoiceNote" -ForegroundColor Yellow }
if ($vaultNote) { Write-Host "Note: $vaultNote" -ForegroundColor Yellow }
Write-Host ""
if ($vaultNote)
{
    Write-Host "Until the bridge vault is registered, .md files outside every vault will" -ForegroundColor Yellow
    Write-Host "fall back to Typora / VS Code / Notepad instead of Obsidian." -ForegroundColor Yellow
}
else
{
    Write-Host ".md files outside every vault are mounted into the bridge vault and open"
    Write-Host "in Obsidian too (first open in a new folder takes a moment while Obsidian"
    Write-Host "picks up the mount)."
}
Write-Host ""
Write-Host "Previous .md default backed up to: $backupFile"
Write-Host "Obsidian vault list backed up to : $vaultBackup"
Write-Host "To undo everything: powershell -ExecutionPolicy Bypass -File .\uninstall.ps1"
