#Requires -Version 7
<#
.SYNOPSIS
  The ONLY way to deploy Autofleet into the game. Gate -> build -> offline load check -> copy -> verify.
.DESCRIPTION
  1. Abort if Cosmoteer is running (files locked / half-loaded mod).
  2. qgate -All -Full (abort on fail).
  3. dotnet build -c Release.
  4. tools/jitcheck: JIT-compile every mod method against the real game assemblies, then call
     GameLoadInitializer() on a temp copy of the CURRENTLY DEPLOYED user data (*.json; read-only).
     Catches load-time crashes (e.g. e4e686b GetText before strings were loaded) before the game sees them.
  5. Copy Autofleet.dll, mod.rules, strings\*.rules into the mod folder. Never touches settings.rules.
  6. Verify SHA256 of every copied file equals the source; print 12-char hashes.
#>
param(
    [string]$ModDir = 'C:\Users\Morgott\Saved Games\Cosmoteer\76561197996210591\Mods\Autofleet',
    [string]$GameBin = 'D:\Steam\steamapps\common\Cosmoteer\Bin'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Fail([string]$msg) { Write-Host "DEPLOY ABORTED: $msg" -ForegroundColor Red; exit 1 }

Step 'game not running'
if (Get-Process -Name Cosmoteer -ErrorAction SilentlyContinue) { Fail 'Cosmoteer is running; close it first.' }
if (-not (Test-Path -LiteralPath $ModDir -PathType Container)) { Fail "mod dir not found: $ModDir" }

Push-Location -LiteralPath $repo
try {
    Step 'qgate -All -Full'
    & qgate -All -Full
    if ($LASTEXITCODE -ne 0) { Fail "qgate failed (exit $LASTEXITCODE)" }

    Step 'dotnet build -c Release'
    & dotnet build (Join-Path $repo 'MiningDrone.csproj') -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail "build failed (exit $LASTEXITCODE)" }
    $dll = Join-Path $repo 'bin\Release\Autofleet.dll'
    if (-not (Test-Path -LiteralPath $dll)) { Fail "build output missing: $dll" }

    Step "jitcheck (JIT + --init against $ModDir)"
    $prevRoll = $env:DOTNET_ROLL_FORWARD
    $env:DOTNET_ROLL_FORWARD = 'LatestMajor'   # game assemblies need the newest runtime (net10)
    try {
        & dotnet run --project (Join-Path $repo 'tools\jitcheck\jitcheck.csproj') -c Release -- $dll --bin $GameBin --init $ModDir
        $jit = $LASTEXITCODE
    } finally { $env:DOTNET_ROLL_FORWARD = $prevRoll }
    if ($jit -ne 0) { Fail "jitcheck failed (exit $jit)" }

    Step 'copy'
    $files = @(
        @{ Src = $dll;                             Dst = Join-Path $ModDir 'Autofleet.dll' }
        @{ Src = Join-Path $repo 'mod.rules';      Dst = Join-Path $ModDir 'mod.rules' }
    )
    $stringsDst = Join-Path $ModDir 'strings'
    New-Item -ItemType Directory -Force -Path $stringsDst | Out-Null
    foreach ($s in Get-ChildItem -LiteralPath (Join-Path $repo 'strings') -Filter '*.rules' -File) {
        $files += @{ Src = $s.FullName; Dst = Join-Path $stringsDst $s.Name }
    }
    foreach ($f in $files) { Copy-Item -LiteralPath $f.Src -Destination $f.Dst -Force }

    Step 'verify SHA256'
    $bad = 0
    foreach ($f in $files) {
        $a = (Get-FileHash -LiteralPath $f.Src -Algorithm SHA256).Hash
        $b = (Get-FileHash -LiteralPath $f.Dst -Algorithm SHA256).Hash
        $state = if ($a -eq $b) { 'ok' } else { $bad++; 'MISMATCH' }
        '{0,-8} {1}  {2}' -f $state, $b.Substring(0, 12).ToLowerInvariant(), $f.Dst | Write-Host
    }
    if ($bad) { Fail "$bad file(s) differ after copy" }
    Write-Host 'DEPLOY OK. First launch: trust the mod libraries in the in-game Mods list (new MVID).' -ForegroundColor Green
}
finally { Pop-Location }
