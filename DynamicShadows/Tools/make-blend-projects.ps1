# Generates the Blender project for every map dumped by EXPORTSCENE that does not have one yet.
#
#   .\DynamicShadows\Tools\make-blend-projects.ps1
#   .\DynamicShadows\Tools\make-blend-projects.ps1 -Map 153   # just one, even if it exists
#
# Maps that already have a .blend are skipped: they may hold modelling work and the generator starts
# from an empty scene. To refresh the camera or the background of one without losing that work there
# is update_field_project.py, which redoes only its own part.

param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [string] $Blender  = 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe',
    [string] $Map      = ''
)

$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'blender\build_field_project.py'
$export = Join-Path $GamePath 'MemoriaSceneExport'

if (-not (Test-Path $Blender)) { throw "Cannot find Blender at: $Blender" }
if (-not (Test-Path $export))  { throw "Nothing dumped in: $export  (enable EXPORTSCENE and enter a map)" }

$target = if ($Map) { Join-Path $export $Map } else { $export }
if (-not (Test-Path $target)) { throw "Does not exist: $target" }

Write-Host "== Generating Blender projects ==" -ForegroundColor Cyan
# No "2>&1": PowerShell 5.1 wraps every stderr line of a native executable in an ErrorRecord and
# treats the command as failed even when it exited with code 0. Blender sends its deprecation
# warnings to stderr, so redirecting turns a harmless warning into an error.
& $Blender --background --factory-startup --python $script -- $target |
    Select-String -Pattern '^mapa |^Mapa |guardado en|desviacion maxima|\*\*\*' |
    ForEach-Object { $_.Line }

if ($LASTEXITCODE -ne 0) { throw "Blender exited with code $LASTEXITCODE" }

Write-Host ""
Write-Host "Done. The .blend files sit next to their field.json, in $export\<map>\" -ForegroundColor Green
Write-Host "If any deviation goes above 1 px, that camera does not reproduce the game's: say so."
