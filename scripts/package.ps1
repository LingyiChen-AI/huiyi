# Builds a self-contained release (no .NET needed on the user's PC) and zips it:
#   powershell -File scripts\package.ps1            -> artifacts\HuiYi-<version>-win-x64.zip
# The speech models are not in the package: the app downloads and checks them on first run.
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }

[xml]$proj = Get-Content "$root\src\HuiYi\HuiYi.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$out = "$root\artifacts\publish"
$zip = "$root\artifacts\HuiYi-$version-win-x64.zip"

Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
& $dotnet publish "$root\src\HuiYi\HuiYi.csproj" -c $Configuration -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Copy-Item "$root\LICENSE", "$root\NOTICE" $out

Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$out\*" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path -Leaf $zip)" | Out-File -Encoding ascii "$zip.sha256"
"{0}  {1:N0} MB  sha256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash
