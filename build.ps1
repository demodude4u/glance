# Builds Glance into .\dist\Glance  (Glance.exe + step\glance-step.exe and its OpenCascade DLLs)
# and the single-file installer .\dist\Glance Setup.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist\Glance'

# The native AOT link step locates MSVC through vswhere.exe, which isn't on PATH by default.
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path "$vsInstaller\vswhere.exe") -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$vsInstaller;$env:PATH" }

dotnet publish "$root\src\Glance\Glance.csproj" -c Release -o "$root\build\glance" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Glance build failed' }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force $dist | Out-Null
Copy-Item "$root\build\glance\Glance.exe" $dist

# Explorer thumbnails: the handler DLL that Windows loads and the helper that draws each picture (see README).
dotnet publish "$root\src\ThumbShell\ThumbShell.csproj" -c Release -o "$root\build\thumbshell" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Thumbnail handler build failed' }
dotnet publish "$root\src\Thumb\Thumb.csproj" -c Release -o "$root\build\thumb" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Thumbnail helper build failed' }
Copy-Item "$root\build\thumbshell\GlanceThumbs.dll" $dist
Copy-Item "$root\build\thumb\glance-thumb.exe" $dist

# Native STEP/IGES reader. OpenCascade (LGPL) is a build dependency; see README for where it comes from.
$occt = Get-ChildItem "$root\build\deps\occt" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $occt) { throw "OpenCascade SDK not found under build\deps\occt (see README, Building)" }
python "$root\tools\build_step.py" $occt.FullName "$root\build\deps\runtime" "$dist\step"
if ($LASTEXITCODE -ne 0) { throw 'STEP reader build failed' }
Copy-Item "$root\licenses\*" $dist                      # third-party notices travel with the program
python "$root\tools\check_deps.py" $dist
if ($LASTEXITCODE -ne 0) { throw 'A shipped file depends on something that is not shipped (see above)' }

Get-ChildItem $dist -Recurse -File | Measure-Object Length -Sum |
    ForEach-Object { "Built $dist  ({0:N1} MB, {1} files)" -f ($_.Sum / 1MB), $_.Count }

# The installer people actually receive: one file with the folder above packed inside it.
$payload = "$root\build\payload.zip"
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path "$dist\*" -DestinationPath $payload -CompressionLevel Optimal
dotnet publish "$root\src\Setup\Setup.csproj" -c Release -o "$root\build\setup" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed' }
Copy-Item "$root\build\setup\Glance Setup.exe" "$root\dist\Glance Setup.exe" -Force
$version = ([xml](Get-Content "$root\src\Directory.Build.props")).Project.PropertyGroup.Version

# What a GitHub release must carry for installed copies to update themselves (see README, Releasing):
# the installer under a name without spaces, and version.txt = the version and the installer's SHA-256.
$release = "$root\dist\release"
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item "$root\dist\Glance Setup.exe" "$release\Glance.Setup.exe"
$hash = (Get-FileHash "$release\Glance.Setup.exe" -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$release\version.txt", "$version`n$hash`n")
"Built $root\dist\Glance Setup.exe  version $version  ({0:N1} MB)" -f ((Get-Item "$root\dist\Glance Setup.exe").Length / 1MB)
