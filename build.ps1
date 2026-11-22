<#
    Publish YuukaOCR into the publish/ folder.

    ./build.ps1                  -> framework-dependent (~0.5 MB, needs the .NET 10 Desktop Runtime)
    ./build.ps1 -SelfContained   -> bundles the runtime (~70 MB, runs on machines without .NET)
    ./build.ps1 -SkipTests       -> skips the test step

    Steps: build -> localization check -> tests -> publish.

    Note: do NOT enable PublishSingleFile for WPF — it drags in the whole runtime and balloons to ~130 MB.
    The framework-dependent folder build is only ~0.5 MB and starts faster.
#>
param(
    [switch]$SelfContained,
    [switch]$SkipTests,
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "== Build ==" -ForegroundColor Cyan
dotnet build YuukaOCR.slnx -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# The test runner only sees tables that are registered, so a StringsXX.cs nobody added to L.Tables
# would ship as dead code: it would compile, pass every test, and never translate anything. Check the
# registration here, where a language is actually added.
Write-Host "== Localization ==" -ForegroundColor Cyan
$locDir = Join-Path $root "src/YuukaOCR.Core/Localization"
$translator = Get-Content (Join-Path $locDir "L.cs") -Raw
$unregistered = @(
    Get-ChildItem $locDir -Filter "Strings*.cs" |
        Where-Object { $translator -notmatch [regex]::Escape("$($_.BaseName).Map") } |
        ForEach-Object { $_.BaseName }
)
if ($unregistered.Count -gt 0) {
    throw ("not registered in L.Tables: " + ($unregistered -join ", "))
}

if (-not $SkipTests) {
    Write-Host "== Test ==" -ForegroundColor Cyan
    dotnet run --project tests/YuukaOCR.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

# API tracing (YUUKA_API_DEBUG=1) writes full prompts and model replies into user/api-debug.log. A
# production build must not carry any of that: delete the trace file and any stale copy next to the exe.
Write-Host "== Debug logs ==" -ForegroundColor Cyan
$traceFiles = @(
    (Join-Path $root "user/api-debug.log"),
    (Join-Path $root "api-debug.log")
)
foreach ($trace in $traceFiles) {
    if (Test-Path $trace) {
        Remove-Item $trace -Force
        Write-Host "  removed $trace"
    }
}
Write-Host "  API tracing is off unless YUUKA_API_DEBUG=1 is set in the environment"

Write-Host "== Publish ==" -ForegroundColor Cyan
$out = Join-Path $root "publish"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$scValue = if ($SelfContained) { "true" } else { "false" }   # PS is case-insensitive: do not reuse the name $SelfContained

dotnet publish src/YuukaOCR.App/YuukaOCR.App.csproj `
    -c Release `
    -r $Runtime `
    -o $out `
    --nologo `
    --self-contained $scValue `
    -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# config/ must sit next to the exe (that is where AppPaths looks for it).
New-Item -ItemType Directory -Force -Path (Join-Path $out "config") | Out-Null
Copy-Item (Join-Path $root "config/app_configs.json") (Join-Path $out "config/app_configs.json") -Force

$size = (Get-ChildItem $out -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("== Done: {0} ({1:N1} MB) ==" -f $out, $size) -ForegroundColor Green
Write-Host "Run: $out\YuukaOCR.exe"
