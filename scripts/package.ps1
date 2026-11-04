# Windows PowerShell 5.1 reads a script without a BOM as the ANSI code page: keep this file UTF-8 with BOM.
# Builds a self-contained release (no .NET needed on the user's PC):
#   powershell -File scripts\package.ps1
#     -> artifacts\HuiYi-Setup.exe                     the installer people download (pick a folder)
#     -> artifacts\releases\HuiYi.App-win-Setup.exe   Velopack's own setup, embedded in the one above
#     -> artifacts\releases\*.nupkg, releases.win.json  what the in-app updater downloads from GitHub
#     -> artifacts\HuiYi-<version>-win-x64.zip         a portable copy (does not update itself)
# The speech models are not in the package: the app downloads and checks them on first run.
# Needs the vpk tool: dotnet tool install -g vpk (same version as the Velopack package in HuiYi.csproj).
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
if (-not $vpk) { $vpk = "$env:USERPROFILE\.dotnet\tools\vpk.exe" }

[xml]$proj = Get-Content "$root\src\HuiYi\HuiYi.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$out = "$root\artifacts\publish"
$releases = "$root\artifacts\releases"
$zip = "$root\artifacts\HuiYi-$version-win-x64.zip"
$repo = "https://github.com/LingyiChen-AI/huiyi"

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
& $dotnet publish "$root\src\HuiYi\HuiYi.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Copy-Item "$root\LICENSE", "$root\NOTICE" $out

New-Item -ItemType Directory -Force $releases | Out-Null

& $vpk pack --packId HuiYi.App --packVersion $version --packDir $out --mainExe HuiYi.exe `
    --packTitle "会译" --packAuthors "HuiYi" --icon "$root\assets\HuiYi.ico" --delta none --runtime win-x64 `
    --outputDir $releases 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

# The installer people download: pick a folder, then it runs Velopack's Setup.exe (embedded).
$installer = "$root\artifacts\HuiYi-Setup.exe"
& $dotnet build "$root\installer\HuiYi.Setup.csproj" -c $Configuration -p:Version=$version `
    -p:VelopackSetup="$releases\HuiYi.App-win-Setup.exe" -o "$root\artifacts\installer" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
Copy-Item "$root\artifacts\installer\HuiYi-Setup.exe" $installer -Force

Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip
foreach ($f in @($installer, $zip)) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    "{0}  {1:N0} MB  sha256 {2}" -f $f, ((Get-Item $f).Length / 1MB), $hash
}
