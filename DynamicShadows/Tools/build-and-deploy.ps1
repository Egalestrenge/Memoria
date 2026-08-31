# Builds Assembly-CSharp and deploys the DLL + the Dynamic Shadows mod into the game.
# Requires PowerShell AS ADMINISTRATOR (the game lives in Program Files).
#
#   .\DynamicShadows\Tools\build-and-deploy.ps1              # build and deploy
#   .\DynamicShadows\Tools\build-and-deploy.ps1 -SkipBuild   # deploy the mod only
#   .\DynamicShadows\Tools\build-and-deploy.ps1 -EditConfig  # also drop the config in the root
#
# Environment notes:
#  - MSBuild from VS 2022 Build Tools is used because the C++ projects ask for
#    toolset v143, which VS 2026 does not include (it only ships v145).
#  - FrameworkPathOverride is needed because Memoria.XInputDotNetPure.csproj is
#    the only v3.5 project without that property and there is no .NET 3.5
#    targeting pack installed.

param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [switch] $SkipBuild,
    # The mod already ships its own MemoriaFieldObjects.txt. This additionally drops a copy in the
    # game root, which takes priority over the mod's: it is how positions, lights and CHARLIGHT get
    # tuned live (it is re-read on every map load) without touching the mod or redeploying.
    [switch] $EditConfig
)

$ErrorActionPreference = 'Stop'

$modName  = 'DynamicShadows'
$tools    = $PSScriptRoot                              # <repo>\DynamicShadows\Tools
$project  = Split-Path -Parent $tools                  # <repo>\DynamicShadows
$repo     = Split-Path -Parent $project                # <repo>  (the Memoria fork)
$output   = Join-Path $repo 'Output'
$modSrc   = Join-Path $project "Mod\$modName"
$msbuild  = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$managed  = Join-Path $GamePath 'x64\FF9_Data\Managed'

if (-not (Test-Path $managed)) { throw "Cannot find the game's Managed folder: $managed" }
if (-not (Test-Path $modSrc))  { throw "Cannot find the mod folder: $modSrc" }

if (-not $SkipBuild) {
    if (-not (Test-Path $msbuild)) { throw "Cannot find MSBuild from VS 2022 Build Tools: $msbuild" }
    Write-Host '== Building Assembly-CSharp ==' -ForegroundColor Cyan
    & $msbuild (Join-Path $repo 'Assembly-CSharp\Assembly-CSharp.csproj') `
        -t:Build -p:Configuration=Release `
        "-p:FrameworkPathOverride=$(Join-Path $repo 'References')\" `
        -v:minimal -nologo -m
    if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE)" }
}

Write-Host '== Deploying DLLs ==' -ForegroundColor Cyan
foreach ($dll in @('Assembly-CSharp.dll', 'Memoria.Prime.dll', 'UnityEngine.UI.dll', 'XInputDotNetPure.dll')) {
    $src = Join-Path $output $dll
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $managed $dll) -Force
        Write-Host "   $dll"
    }
}

Write-Host "== Deploying the $modName mod ==" -ForegroundColor Cyan
$modDst = Join-Path $GamePath $modName
# Copy-Item -Recurse onto a folder that already exists nests a copy inside instead of merging, so
# the destination is removed first. Only the mod folder is removed.
if (Test-Path $modDst) { Remove-Item $modDst -Recurse -Force }
Copy-Item $modSrc $modDst -Recurse -Force
Write-Host "   $modName\ -> $modDst"

$rootConfig = Join-Path $GamePath 'MemoriaFieldObjects.txt'
if ($EditConfig) {
    Copy-Item (Join-Path $modSrc 'MemoriaFieldObjects.txt') $rootConfig -Force
    Write-Host "   MemoriaFieldObjects.txt copied to the game root for live editing"
    Write-Host "   Edit it at: $rootConfig" -ForegroundColor DarkGray
} elseif (Test-Path $rootConfig) {
    Write-Warning "There is a MemoriaFieldObjects.txt in the game root and it takes PRIORITY over"
    Write-Warning "the mod's. If you expected the freshly deployed config, delete: $rootConfig"
}

$ini = Join-Path $GamePath 'Memoria.ini'
if (Test-Path $ini) {
    $folderLine = Select-String -Path $ini -Pattern '^\s*FolderNames' -ErrorAction SilentlyContinue
    if ($folderLine -and $folderLine.Line -notmatch $modName) {
        Write-Warning "Memoria.ini: [Mod] FolderNames does not include `"$modName`"; the mod will not load."
        Write-Warning "   It currently reads -> $($folderLine.Line.Trim())"
        Write-Warning "   Add it there, or enable the mod from the launcher's Mod Manager."
    }
} else {
    Write-Warning "$ini does not exist yet: launch the game once after patching."
}

Write-Host 'Done.' -ForegroundColor Green
