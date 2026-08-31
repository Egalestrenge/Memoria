# Generates the Blender project for every map dumped by EXPORTSCENE that does not have one yet.
#
#   .\DynamicShadows\Tools\make-blend-projects.ps1
#   .\DynamicShadows\Tools\make-blend-projects.ps1 -Map 153   # just one, even if it exists
#
# Maps that already have a .blend are skipped: they may hold modelling work and the generator starts
# from an empty scene. To refresh the camera or the background of one without losing that work there
# is update_field_project.py, which redoes only its own part.
#
# A .blend older than its field.json is STALE: the map was re-exported afterwards, most often
# because the game was played at a different resolution, and the camera, the render resolution and
# the background image in the project are the previous ones. Nothing errors, the viewport simply
# stops matching the game. Those are reported at the end, and -Update refreshes them.

param(
    [string] $GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [string] $Blender  = 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe',
    [string] $Map      = '',
    # Refresh the projects whose export is newer than their .blend, with update_field_project.py.
    # Safe on work in progress: it only rebuilds the objects the tool itself generates.
    [switch] $Update
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
    Select-String -Pattern '^map |^Map |saved to|median deviation|\*\*\*' |
    ForEach-Object { Write-Host $_.Line }

if ($LASTEXITCODE -ne 0) { throw "Blender exited with code $LASTEXITCODE" }

Write-Host ""
Write-Host "Done. The .blend files sit next to their field.json, in $export\<map>\" -ForegroundColor Green
Write-Host "If any deviation goes above 1 px, that camera does not reproduce the game's: say so."

# Projects whose export is newer than the .blend built from it. The generator above skipped
# them on purpose -they may hold modelling- so without this they would stay silently stale.
$updater = Join-Path $PSScriptRoot 'blender\update_field_project.py'
$stale = @()
foreach ($dir in Get-ChildItem $target -Directory -ErrorAction SilentlyContinue) {
    $json = Join-Path $dir.FullName 'field.json'
    if (-not (Test-Path $json)) { continue }
    # Every .blend of the map, the _edit ones included: those are usually where the modelling
    # lives, so skipping them would ignore exactly the files that matter.
    foreach ($blend in Get-ChildItem $dir.FullName -Filter 'field_*.blend' -ErrorAction SilentlyContinue) {
        if ((Get-Item $json).LastWriteTime -gt $blend.LastWriteTime) {
            $stale += [PSCustomObject]@{ Map = $dir.Name; Blend = $blend.FullName; Folder = $dir.FullName }
        }
    }
}

if ($stale.Count -gt 0) {
    Write-Host ""
    Write-Host "== $($stale.Count) project(s) older than their export ==" -ForegroundColor Yellow
    foreach ($x in $stale) { Write-Host "   map $($x.Map): $(Split-Path $x.Blend -Leaf)" }
    Write-Host "The map was re-exported after the project was built -typically after playing at a"
    Write-Host "different resolution- so its camera, resolution and background are the old ones."
    if (-not $Update) {
        Write-Host "Re-run with -Update to refresh them. Your modelling is preserved." -ForegroundColor Yellow
    } else {
        Write-Host ""
        Write-Host "== Refreshing ==" -ForegroundColor Cyan
        foreach ($x in $stale) {
            Write-Host "   map $($x.Map)"
            & $Blender --background --factory-startup --python $updater -- $x.Blend $x.Folder |
                Select-String -Pattern 'rebuilt|background|camera|WRONG|\*\*\*' |
                ForEach-Object { '   ' + $_.Line }
            if ($LASTEXITCODE -ne 0) { throw "Blender exited with code $LASTEXITCODE on map $($x.Map)" }
        }
    }
}
