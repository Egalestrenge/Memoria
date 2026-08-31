# Compila Assembly-CSharp y despliega la DLL + el mod Dynamic Shadows en el juego.
# Requiere PowerShell COMO ADMINISTRADOR (el juego esta en Program Files).
#
#   .\DynamicShadows\Tools\build-and-deploy.ps1              # compila y despliega
#   .\DynamicShadows\Tools\build-and-deploy.ps1 -SkipBuild   # solo despliega el mod
#   .\DynamicShadows\Tools\build-and-deploy.ps1 -EditConfig  # ademas saca la config a la raiz
#
# Notas del entorno:
#  - Se usa el MSBuild de VS 2022 Build Tools porque los proyectos C++ piden
#    el toolset v143, que VS 2026 no incluye (solo trae v145).
#  - FrameworkPathOverride hace falta porque Memoria.XInputDotNetPure.csproj es
#    el unico proyecto v3.5 sin esa propiedad y no hay targeting pack de .NET 3.5.

param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [switch] $SkipBuild,
    # El mod ya trae su MemoriaFieldObjects.txt. Con esto se saca ademas una copia a la raiz del
    # juego, que tiene prioridad sobre la del mod: es la forma de ajustar posiciones, luces y
    # CHARLIGHT en caliente (se relee al cargar cada mapa) sin tocar el mod ni redesplegar.
    [switch] $EditConfig
)

$ErrorActionPreference = 'Stop'

$modName  = 'DynamicShadows'
$tools    = $PSScriptRoot                              # <repo>\DynamicShadows\Tools
$project  = Split-Path -Parent $tools                  # <repo>\DynamicShadows
$repo     = Split-Path -Parent $project                # <repo>  (el fork de Memoria)
$output   = Join-Path $repo 'Output'
$modSrc   = Join-Path $project "Mod\$modName"
$msbuild  = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$managed  = Join-Path $GamePath 'x64\FF9_Data\Managed'

if (-not (Test-Path $managed)) { throw "No encuentro la carpeta Managed del juego: $managed" }
if (-not (Test-Path $modSrc))  { throw "No encuentro la carpeta del mod: $modSrc" }

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

Write-Host "== Desplegando el mod $modName ==" -ForegroundColor Cyan
$modDst = Join-Path $GamePath $modName
# Copy-Item -Recurse sobre una carpeta que ya existe anida una copia dentro en lugar de
# fusionarla, asi que se borra el destino primero. Solo se borra la carpeta del mod.
if (Test-Path $modDst) { Remove-Item $modDst -Recurse -Force }
Copy-Item $modSrc $modDst -Recurse -Force
Write-Host "   $modName\ -> $modDst"

$rootConfig = Join-Path $GamePath 'MemoriaFieldObjects.txt'
if ($EditConfig) {
    Copy-Item (Join-Path $modSrc 'MemoriaFieldObjects.txt') $rootConfig -Force
    Write-Host "   MemoriaFieldObjects.txt copiado a la raiz para editar en caliente"
    Write-Host "   Editalo en: $rootConfig" -ForegroundColor DarkGray
} elseif (Test-Path $rootConfig) {
    Write-Warning "Hay un MemoriaFieldObjects.txt en la raiz del juego y tiene PRIORIDAD sobre el"
    Write-Warning "del mod. Si esperabas ver la config recien desplegada, borra: $rootConfig"
}

$ini = Join-Path $GamePath 'Memoria.ini'
if (Test-Path $ini) {
    $folderLine = Select-String -Path $ini -Pattern '^\s*FolderNames' -ErrorAction SilentlyContinue
    if ($folderLine -and $folderLine.Line -notmatch $modName) {
        Write-Warning "Memoria.ini: [Mod] FolderNames no incluye `"$modName`" y el mod no se cargara."
        Write-Warning "   Ahora vale -> $($folderLine.Line.Trim())"
        Write-Warning "   Anadelo ahi, o activa el mod desde el Mod Manager del launcher."
    }
} else {
    Write-Warning "No existe $ini todavia: lanza el juego una vez despues de parchear."
}

Write-Host 'Listo.' -ForegroundColor Green
