#Requires -Version 5.1
<#
  Recreates the two reported failures without WeChat or WPS, and prints a
  timeline of the measured taskbar state.

    .\test\repro-scenario.ps1

    phase1  a maximized window takes the foreground      -> taskbar hides
    phase2  a small popup takes the foreground           -> taskbar shows
    phase3  the popup hides, foreground is left on a
            hidden window of a still-running process     -> taskbar hides again

  phase3 is the reported bug: with v1.0 the last line stays "shown".

  Needs taskbar-autohide.exe running and popup-repro.exe + appbar-state.ps1
  next to this script. ASCII only (PowerShell 5.1 reads BOM-less UTF-8 as ANSI).
#>
param(
    [int]$FrameMs = 6000,
    [int]$PopupMs = 4000
)

$ErrorActionPreference = 'Stop'

$here  = $PSScriptRoot
$repro = Join-Path $here 'popup-repro.exe'
$probe = Join-Path $here 'appbar-state.ps1'
foreach ($f in @($repro, $probe)) {
    if (-not (Test-Path $f)) { throw "missing: $f (build it first)" }
}

function State { (& $probe).Trim() }

$frame = $null
$pop   = $null
try {
    $frame = Start-Process $repro -ArgumentList 'frame', $FrameMs -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 1500
    'phase1 maximized foreground  : ' + (State)

    $pop = Start-Process $repro -ArgumentList 'popup', $PopupMs -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 1000
    'phase2 popup visible         : ' + (State)
    Start-Sleep -Milliseconds 700          # the popup hides at ~1.5 s
    'phase3 popup gone (after 0 s): ' + (State)
    Start-Sleep -Milliseconds 2500
    'phase3 popup gone (settled)  : ' + (State)
}
finally {
    foreach ($p in @($pop, $frame)) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
    Start-Sleep -Milliseconds 1200
    'after cleanup                : ' + (State)
}
