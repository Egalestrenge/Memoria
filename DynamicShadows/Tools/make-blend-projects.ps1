# Genera el proyecto de Blender de todos los mapas volcados por EXPORTSCENE que aun no lo tengan.
#
#   .\tools\make-blend-projects.ps1
#   .\tools\make-blend-projects.ps1 -Map 153        # solo uno, aunque ya exista
#
# Los que ya tienen .blend se saltan: pueden llevar modelado dentro y el generador arranca de una
# escena vacia. Para refrescar la camara o el fondo de uno sin perder el trabajo esta
# update_field_project.py, que rehace solo lo suyo.

param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [string] $Blender  = 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe',
    [string] $Map      = ''
)

$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'blender\build_field_project.py'
$export = Join-Path $GamePath 'MemoriaSceneExport'

if (-not (Test-Path $Blender)) { throw "No encuentro Blender en: $Blender" }
if (-not (Test-Path $export))  { throw "No hay nada volcado en: $export  (activa EXPORTSCENE y entra a un mapa)" }

$target = if ($Map) { Join-Path $export $Map } else { $export }
if (-not (Test-Path $target)) { throw "No existe: $target" }

Write-Host "== Generando proyectos de Blender ==" -ForegroundColor Cyan
# Sin "2>&1": PowerShell 5.1 envuelve cada linea de stderr de un ejecutable nativo en un
# ErrorRecord y da el comando por fallido aunque haya terminado con codigo 0. Blender manda por
# stderr sus avisos de deprecacion, asi que redirigirlo convierte un aviso inofensivo en un error.
& $Blender --background --factory-startup --python $script -- $target |
    Select-String -Pattern '^mapa |^Mapa |guardado en|desviacion maxima|\*\*\*' |
    ForEach-Object { $_.Line }

if ($LASTEXITCODE -ne 0) { throw "Blender termino con codigo $LASTEXITCODE" }

Write-Host ""
Write-Host "Listo. Los .blend estan junto a su field.json, en $export\<mapa>\" -ForegroundColor Green
Write-Host "Si alguna desviacion pasa de 1 px, esa camara no reproduce la del juego: avisa."
