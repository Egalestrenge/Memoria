# Compila Assembly-CSharp y despliega la build + el mod de pruebas en el juego.
# Requiere PowerShell COMO ADMINISTRADOR (el juego esta en Program Files).
#
#   .\tools\build-and-deploy.ps1              # compila y despliega
#   .\tools\build-and-deploy.ps1 -SkipBuild   # solo copia el mod y la config
#
# Notas:
#  - Se usa el MSBuild de VS 2022 Build Tools porque los proyectos C++ piden
#    el toolset v143, que VS 2026 no incluye (solo trae v145).
#  - FrameworkPathOverride hace falta porque Memoria.XInputDotNetPure.csproj es
#    el unico proyecto v3.5 sin esa propiedad y no hay targeting pack de .NET 3.5.

param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [switch] $SkipBuild,
    # MemoriaFieldObjects.txt se edita en la carpeta del juego (se relee al cargar cada mapa),
    # asi que no se sobrescribe salvo que lo pidas explicitamente.
    [switch] $ResetConfig
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
$repo     = Join-Path $root 'Memoria'
$output   = Join-Path $repo 'Output'
$msbuild  = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$managed  = Join-Path $GamePath 'x64\FF9_Data\Managed'

if (-not (Test-Path $managed)) { throw "No encuentro la carpeta Managed del juego: $managed" }

if (-not $SkipBuild) {
    if (-not (Test-Path $msbuild)) { throw "No encuentro MSBuild de VS 2022 Build Tools: $msbuild" }
    Write-Host '== Compilando Assembly-CSharp ==' -ForegroundColor Cyan
    & $msbuild (Join-Path $repo 'Assembly-CSharp\Assembly-CSharp.csproj') `
        -t:Build -p:Configuration=Release `
        "-p:FrameworkPathOverride=$(Join-Path $repo 'References')\" `
        -v:minimal -nologo -m
    if ($LASTEXITCODE -ne 0) { throw "La compilacion ha fallado (codigo $LASTEXITCODE)" }
}

Write-Host '== Desplegando DLLs ==' -ForegroundColor Cyan
foreach ($dll in @('Assembly-CSharp.dll', 'Memoria.Prime.dll', 'UnityEngine.UI.dll', 'XInputDotNetPure.dll')) {
    $src = Join-Path $output $dll
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $managed $dll) -Force
        Write-Host "   $dll"
    }
}

Write-Host '== Desplegando mod y configuracion ==' -ForegroundColor Cyan
Copy-Item (Join-Path $root 'TestScenario') $GamePath -Recurse -Force
Write-Host '   TestScenario\'

$configTarget = Join-Path $GamePath 'MemoriaFieldObjects.txt'
if ($ResetConfig -or -not (Test-Path $configTarget)) {
    Copy-Item (Join-Path $root 'MemoriaFieldObjects.txt') $configTarget -Force
    Write-Host '   MemoriaFieldObjects.txt'
} else {
    Write-Host "   MemoriaFieldObjects.txt (conservado; usa -ResetConfig para sobrescribir)"
}
Write-Host "   Edita las posiciones en: $configTarget" -ForegroundColor DarkGray

$ini = Join-Path $GamePath 'Memoria.ini'
if (Test-Path $ini) {
    $folderLine = Select-String -Path $ini -Pattern '^\s*FolderNames' -ErrorAction SilentlyContinue
    if ($folderLine -and $folderLine.Line -notmatch 'TestScenario') {
        Write-Warning "Memoria.ini: revisa [Mod] FolderNames, ahora vale -> $($folderLine.Line.Trim())"
        Write-Warning 'Debe incluir "TestScenario" para que el mod se cargue.'
    }
} else {
    Write-Warning "No existe $ini todavia: lanza el juego una vez despues de parchear."
}

Write-Host 'Listo.' -ForegroundColor Green
