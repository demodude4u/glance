# Installs Glance for the current user (no admin rights needed) with both options on:
# open model files with Glance, and the right-click "Open in Glance" entry.
# Run .\build.ps1 first. For the window with the two tick boxes, run "dist\Glance Setup.exe" directly.
$ErrorActionPreference = 'Stop'
$setup = Join-Path $PSScriptRoot 'dist\Glance Setup.exe'
if (-not (Test-Path $setup)) { throw "Nothing to install: run .\build.ps1 first." }
$p = Start-Process -FilePath $setup -ArgumentList '--quiet' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "Glance Setup failed (exit code $($p.ExitCode))." }
"Installed to $(Join-Path $env:LOCALAPPDATA 'Programs\Glance')"
