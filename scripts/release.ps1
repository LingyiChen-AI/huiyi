# Windows PowerShell 5.1 reads a script without a BOM as the ANSI code page: keep this file UTF-8 with BOM.
# Starts a release:  powershell -File scripts\release.ps1 0.2.0
# Sets the version, runs the tests, commits, tags v<version> and pushes. The tag triggers
# .github/workflows/release.yml, which builds the installer and publishes the GitHub release that
# installed copies update from.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe" }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version must look like 1.2.3" }
if (git -C $root status --porcelain) { throw "commit or stash your changes first" }
if (git -C $root tag -l "v$Version") { throw "v$Version already exists" }

$csproj = "$root\src\HuiYi\HuiYi.csproj"
$text = [IO.File]::ReadAllText($csproj)
$text = [regex]::Replace($text, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($csproj, $text)

& $dotnet test "$root\tests\HuiYi.Tests" -v q --nologo
if ($LASTEXITCODE -ne 0) { git -C $root checkout -- $csproj; throw "tests failed" }

if (git -C $root status --porcelain) {
    git -C $root add $csproj
    git -C $root commit -q -m "chore: 发布 $Version"
}
git -C $root tag "v$Version"
git -C $root push -q origin HEAD "v$Version"
"pushed v${Version}, the release workflow is building it -> https://github.com/LingyiChen-AI/huiyi/actions"
