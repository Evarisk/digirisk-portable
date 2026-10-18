$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$src = Join-Path $PSScriptRoot "Launcher.cs"
$manifest = Join-Path $PSScriptRoot "app.manifest"
$ico = Join-Path $root "App\AppInfo\appicon.ico"

$outRootExe = Join-Path $root "DigiRiskPortable.exe"
$outAppExe  = Join-Path $root "App\DigiRisk\DigiRiskApp.exe"

& $csc /nologo /target:winexe /optimize+ `
    /win32icon:"$ico" `
    /win32manifest:"$manifest" `
    /r:System.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll `
    /out:"$outRootExe" "$src"

Copy-Item -Force "$outRootExe" "$outAppExe"
Write-Host "Built successfully: $outRootExe and $outAppExe"
