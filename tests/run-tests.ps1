# Run the unit tests for OpenInObsidian.exe.
#
#   powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
#
# Compiles src\OpenInObsidian.cs (exactly like install.ps1 does) plus
# tests\TestDriver.cs into a temporary directory, runs the driver, and
# exits non-zero when any test fails. The temp directory is removed
# afterwards; nothing outside it is touched.
#
# NOTE: the csc.exe lookup below mirrors the one in scripts\install.ps1.
# Keep the two in sync — if you change the compiler path here, change it
# there too (or factor both into a shared helper).

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ("oio-tests-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

try
{
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc))
    {
        $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $csc))
    {
        throw "csc.exe not found (expected in $env:WINDIR\Microsoft.NET\Framework64\v4.0.30319)."
    }

    # Compile the forwarder exactly like install.ps1 does.
    & $csc /nologo /target:winexe /optimize+ "/r:System.Web.Extensions.dll" "/out:$work\OpenInObsidian.exe" (Join-Path $repoRoot 'src\OpenInObsidian.cs')
    if ($LASTEXITCODE -ne 0) { throw 'compiling src\OpenInObsidian.cs failed.' }

    # Compile the reflection-based test driver (console exe so output is visible).
    & $csc /nologo /target:exe "/out:$work\test.exe" (Join-Path $PSScriptRoot 'TestDriver.cs')
    if ($LASTEXITCODE -ne 0) { throw 'compiling tests\TestDriver.cs failed.' }

    & "$work\test.exe"
    exit $LASTEXITCODE
}
finally
{
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
