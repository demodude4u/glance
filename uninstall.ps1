# Removes Glance for the current user: file associations, right-click entry, Start menu entry,
# program files, settings and cache. Same as Settings > Apps > Installed apps > Glance > Uninstall.
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Glance\Glance.exe'
if (-not (Test-Path $exe)) { "Glance is not installed."; return }
Start-Process -FilePath $exe -ArgumentList '--uninstall', '--quiet' -Wait
"Glance has been removed."
