<#
.SYNOPSIS
    Runs tools/GameHarness: the shipped swap code against the game's real inventory classes.

.DESCRIPTION
    Nothing touches a game install or a profile. Everything is built under -Work, using the game's
    mono runtime only as a program to host the test. Only a mirror with a patched Assembly-CSharp
    will do; see CLAUDE.md for how to build one.

.EXAMPLE
    scripts\run-harness.ps1 -SPTPath <mirror> -Mono C:\HUH\MonoBleedingEdge -Work <empty folder>
#>
param(
    [Parameter(Mandatory = $true)][string] $SPTPath,
    [Parameter(Mandatory = $true)][string] $Mono,
    [Parameter(Mandatory = $true)][string] $Work,
    [string[]] $ExtraAssemblies = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hbin = Join-Path $Work 'hbin'
$mhost = Join-Path $Work 'mhost'
$asm = Join-Path $Work 'asm'
New-Item -ItemType Directory -Force -Path $hbin, $mhost, $asm | Out-Null

dotnet build (Join-Path $root 'tools\GameHarness\GameHarness.csproj') -c Release "-p:SPTPath=$SPTPath" -o $hbin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Harness build failed.' }
dotnet build (Join-Path $root 'tools\MonoHost\MonoHost.csproj') -c Release -o $mhost --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'MonoHost build failed.' }

robocopy (Join-Path $SPTPath 'EscapeFromTarkov_Data\Managed') $asm /E /NFL /NDL /NJH /NJS /NP | Out-Null
robocopy (Join-Path $SPTPath 'BepInEx\core') $asm /E /NFL /NDL /NJH /NJS /NP | Out-Null
Copy-Item (Join-Path $hbin 'GameHarness.exe') $asm -Force
foreach ($extra in $ExtraAssemblies) { Copy-Item $extra $asm -Force }

& (Join-Path $mhost 'MonoHost.exe') (Join-Path $Mono 'EmbedRuntime') (Join-Path $Mono 'etc') $asm (Join-Path $asm 'GameHarness.exe')
$code = $LASTEXITCODE
Write-Host "Harness exit code: $code"
exit $code
