# Windows PowerShell 5.1 reads a script without a BOM as the ANSI code page: keep this file UTF-8 with BOM.
# Publishes a version to GitHub Releases, where installed copies find it within six hours:
#   powershell -File scripts\release.ps1 0.2.0
# Sets the version, runs the tests, packages, commits and tags, pushes, then uploads the installer
# and update packages as release v<version>. Uses the gh login for the token.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Split-Path -Parent $dotnet }
$vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
if (-not $vpk) { $vpk = "$env:USERPROFILE\.dotnet\tools\vpk.exe" }
$repo = "https://github.com/LingyiChen-AI/huiyi"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version must look like 1.2.3" }
if (git -C $root status --porcelain) { throw "commit or stash your changes first" }

$csproj = "$root\src\HuiYi\HuiYi.csproj"
$text = [IO.File]::ReadAllText($csproj)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $text)

& $dotnet test "$root\tests\HuiYi.Tests" -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\scripts\package.ps1"
if ($LASTEXITCODE -ne 0) { throw "package failed" }

git -C $root add $csproj
git -C $root commit -q -m "chore: release $Version"
git -C $root tag "v$Version"
git -C $root push -q origin HEAD "v$Version"

$token = (gh auth token).Trim()
& $vpk upload github --repoUrl $repo --token $token --outputDir "$root\artifacts\releases" `
    --tag "v$Version" --releaseName "会译 $Version" --publish --merge
if ($LASTEXITCODE -ne 0) { throw "upload failed" }
"released v$Version -> $repo/releases/tag/v$Version"
