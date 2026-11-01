# Runs a profile scenario on the Release+PROFILE build, copies each session aside and summarizes it with a preset.
#   -Preset   App (every step, every thread), Animation (animation zones and counters), UI (layout and text on Main)
#   -Out      folder that receives run1, run2 ...
#   -HostApp  Thorium or Carbon
#   -Runs     how many runs
#   -From     first frame index summarized
#   -Top      at most this many zones per thread
#   -Build    build Release+PROFILE first
param(
    [Parameter(Mandatory = $true)][ValidateSet('App', 'Animation', 'UI')][string]$Preset,
    [Parameter(Mandatory = $true)][string]$Out,
    [ValidateSet('Thorium', 'Carbon')][string]$HostApp = 'Thorium',
    [int]$Runs = 3,
    [long]$From = 0,
    [int]$Top = 40,
    [switch]$Build
)

$presets = @{
    App       = @{ Args = '--profile-scenario'; Thread = $null; Zone = @('Step.') }
    Animation = @{ Args = '--profile-scenario=animation'; Thread = $null; Zone = @('Anim.', 'Step.Animation.Step', 'Scenario.') }
    UI        = @{ Args = '--profile-scenario'; Thread = 'Main'
                   Zone = @('Layout.', 'Text.', 'Document.', 'Editor.', 'HandleUI', 'ResolveLayout', 'Step.Main.Layout',
                            'Step.Main.DrawLists', 'Step.Edge.UIElements', 'Root') }
}
$p = $presets[$Preset]

$root = (Resolve-Path (Join-Path $PSScriptRoot (Join-Path '..' (Join-Path '..' '..')))).Path
if ($IsLinux) {
    $bin = Join-Path $root (Join-Path $HostApp 'bin/Release/net10.0')
    $exe = Join-Path $bin $HostApp
}
else {
    $bin = Join-Path $root "$HostApp\bin\Release\net10.0-windows10.0.22621.0"
    $exe = Join-Path $bin "$HostApp.exe"
}
$captures = Join-Path ([Environment]::GetFolderPath('ApplicationData')) (Join-Path 'Arktis' (Join-Path $HostApp 'Profiling'))
$summarize = Join-Path $PSScriptRoot 'summarize.ps1'

if ($Build) {
    if ($IsLinux) {
        dotnet build (Join-Path $root "$HostApp/$HostApp.csproj") -f net10.0 -c Release "-p:DefineConstants=TRACE%3BPROFILE" -p:EnableWindowsTargeting=true -v q -nologo
    }
    else {
        dotnet build (Join-Path $root 'AuroraEngine\ArctisAurora.sln') -c Release "-p:DefineConstants=TRACE%3BPROFILE" -v q -nologo
    }
    if ($LASTEXITCODE -ne 0) { Write-Output "build failed"; exit 1 }
}
if (-not (Test-Path -LiteralPath $exe)) { Write-Output "no $exe - pass -Build"; exit 1 }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

for ($run = 1; $run -le $Runs; $run++) {
    $dest = Join-Path $Out "run$run"
    if (Test-Path -LiteralPath $dest) { Write-Output "$dest exists - pick another -Out"; exit 1 }
    $before =@(if (Test-Path -LiteralPath $captures) { Get-ChildItem -LiteralPath $captures -Directory | ForEach-Object Name })
    if ($IsLinux) {
        $env:VK_ICD_FILENAMES = '/usr/share/vulkan/icd.d/lvp_icd.json'
        Start-Process -FilePath 'xvfb-run' -ArgumentList @('-a', '-s', '"-screen 0 1920x1080x24"', $exe, $p.Args) -WorkingDirectory $bin -Wait
    }
    else { Start-Process -FilePath $exe -ArgumentList $p.Args -WorkingDirectory $bin -Wait }
    $session = Get-ChildItem -LiteralPath $captures -Directory -ErrorAction SilentlyContinue |
        Where-Object { $before -notcontains $_.Name } | Sort-Object Name | Select-Object -Last 1
    if (-not $session) { Write-Output "run $run - no new session in $captures"; exit 1 }

    Copy-Item -LiteralPath $session.FullName -Destination $dest -Recurse

    Write-Output "##### $Preset run $run - $($session.Name) -> $dest"
    if ($p.Thread) { & $summarize -Dir $dest -Thread $p.Thread -Zone $p.Zone -From $From -Top $Top }
    else { & $summarize -Dir $dest -Zone $p.Zone -From $From -Top $Top }
}
