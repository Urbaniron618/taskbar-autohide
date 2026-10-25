#Requires -Version 5.1
<#
  Compiles taskbar-autohide.cs with the C# compiler that ships with .NET
  Framework - no SDK, no NuGet, nothing to install.
  ASCII only on purpose: Windows PowerShell 5.1 reads a BOM-less UTF-8 script
  as ANSI, so non-ASCII here would corrupt parsing on some machines.
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot 'taskbar-autohide.exe')
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc (is .NET Framework 4.x installed?)" }

$src = Join-Path $PSScriptRoot 'taskbar-autohide.cs'
& $csc /nologo /optimize+ /target:winexe "/out:$Out" $src
if ($LASTEXITCODE -ne 0) { throw "compile failed (exit $LASTEXITCODE)" }

Get-Item $Out | Select-Object FullName, Length, LastWriteTime | Format-List
