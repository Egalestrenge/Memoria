# Dynamic Shadows — 3D scenery in Final Fantasy IX on top of Memoria

Detailed handover document (see [README.md](../README.md) for installation and the map workflow).
It describes the goal, what is built and verified, the workflow, and the traps
that have already cost us time. Everything claimed here as "verified" was checked with numbers
during development, not by eye.

---

## 0. How the repo is laid out

This repo **is** the Memoria fork: the 3D pass cannot be a normal mod, because Memoria's mod system
loads data and not code. So the engine and the content live together, but kept apart in the tree:

```
Assembly-CSharp/Memoria/Field/     the 3D pass code (what gets compiled into the DLL)
  CustomFieldObjects.cs            per-map configuration, object spawning, diagnostics
  FieldPerspectiveCamera.cs        camera derived from BGCAM_DEF, shadows, player proxy
  FieldSceneBundle.cs              loading each map's Unity bundle
  FieldSceneExport.cs              dumping a map for Blender (EXPORTSCENE)

DynamicShadows/                    everything that is not engine code
  NOTES.md                         this document
  Mod/DynamicShadows/              the mod exactly as it gets installed
  Unity/DynamicShadows/            Unity 5.2.3f1 project where the scenes are lit
  Tools/                           build, Blender generators and utilities
```

Apart from those four files, the fork touches only **9 lines** of Memoria: five hooks in
`Global/Honolulu/HonoluluFieldMain.cs` and four `<Compile Include>` entries in the `.csproj`.
Keeping it this small is deliberate: it is what allows rebasing onto `upstream/main` painlessly.

### Installing

1. `.\DynamicShadows\Tools\build-and-deploy.ps1` (PowerShell **as administrator**: the game lives
   in Program Files). It builds the DLL, copies it into `x64\FF9_Data\Managed\` and deploys
   `Mod/DynamicShadows/` into the game root.
2. Enable **Dynamic Shadows** in the launcher's Mod Manager, or add it by hand to `Memoria.ini`,
   section `[Mod]`, `FolderNames`. The script warns when it is missing.

The mod ships its own `MemoriaFieldObjects.txt`. A copy in the game root takes priority over the
mod's and is re-read on every map load: that is the route for tuning positions, lights and
`CHARLIGHT` live without redeploying. `-EditConfig` puts one there.

> **What stops it being distributed as a normal mod.** The Mod Manager installs data folders; it
> does not load assemblies. The 3D pass lives in `Assembly-CSharp.dll`, so a release has to ship the
> DLL and is incompatible with any other mod that also replaces it. The clean way out is for the
> code to end up *inside* Memoria via an upstream PR: this mod would then become data only and stop
> having that conflict.

---

## 1. Goal

Replace FFIX's prerendered backgrounds with real 3D scenery, modelled in Blender and lit in Unity,
with the character integrated into it: lit by the same lights, casting a shadow onto the geometry
and occluding correctly against it.

The test map is **150 — `Cast. Alex./Guard`** (the Alexandria Castle guard barracks), small and with
a Steiner save available.

---

## 2. Environment

| Piece        | Detail                                                                         |
| ------------ | ------------------------------------------------------------------------------ |
| Game         | FF9 from Steam, `C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX` |
| Engine       | **Unity 5.2.3f1** (per the `FileVersion` of `x64\FF9.exe`)                     |
| Memoria      | this repo: a fork of `Albeoris/Memoria`, branch `dynamic-shadows`              |
| Unity Editor | 5.2.3f1, project in `DynamicShadows/Unity/DynamicShadows/`                     |
| Blender      | 5.1 at `C:\Program Files\Blender Foundation\Blender 5.1`                       |

### Building

Memoria **is not a plugin**: it is the game's own `Assembly-CSharp.dll`, rewritten. There is no
Harmony and no BepInEx in the repo; methods are edited directly in the decompiled source.

```powershell
.\DynamicShadows\Tools\build-and-deploy.ps1              # build and deploy
.\DynamicShadows\Tools\build-and-deploy.ps1 -EditConfig  # also drop the config in the game root
.\DynamicShadows\Tools\build-and-deploy.ps1 -SkipBuild   # deploy the mod only
```

Two environment quirks, already handled inside the script:

- It uses **MSBuild from VS 2022 Build Tools**, not the VS 2026 one: the C++ projects (`SaXAudio`,
  `Memoria.Injection`) ask for toolset `v143`, and VS 2026 only ships `v145`.
- `-p:FrameworkPathOverride=<repo>\References\` is needed because
  `Memoria.XInputDotNetPure.csproj` is the only v3.5 project without that property, and there is no
  .NET 3.5 targeting pack installed.

---

## 3. The three coordinate systems

This is the heart of everything. **The game is authoritative**; Blender and Unity are views of it.

### Field space (authoritative)

FFIX's internal units. **+Y is up** (confirmed by `FieldMap.charAimHeight`, which is _added_ to
raise the camera's aim point, and by `PSX.CalculateGTE_RTPT`, which negates Y precisely in order to
convert to the PSX Y-down convention).

**Scale: 345 field units per metre.** This is not an estimate: it comes from
`FF9BattleDBHeightAndRadius`, which gives each model's height. Steiner (`GEO_MAIN_F0_STN`, GEO id
5489) is **603 units** tall:

```
factor = 603 / height_in_metres    ->    603 / 1.75 = 345
```

### The camera basis carries scale

`BGCAM_DEF` does not store a pure rotation. The basis exported in `field.json` is orthogonal but
**not orthonormal**: `|right| ≈ 1`, `|forward| ≈ 1`, but **`|up| = 1.0713 = 15/14`**, the PSX
320×224 to 4:3 stretch. Anything reconstructing this camera has to decompose it into
rotation × scale and move the scale into the field of view, **and use the inverse, not the
transpose**. See §5.2.

### Blender space

`field → blender:  (-x, -z, y) / 345`

The Y↔Z permutation is the change of handedness (field is left-handed with Y up, Blender
right-handed with Z up). The **X and Z negations compensate for a 180° rotation about the vertical
axis introduced by the FBX export chain**, measured with markers (§7).

### Unity space

Modelling happens in **metres**, and the runtime multiplies by `SCENESCALE` on load. The objects go
under a `Field3D Scene` container carrying that scale.

> **Why metric and not field units:** field scale breaks every Unity default that works "per unit".
> Baking lightmaps with `Baked Resolution` = 40 texels/unit over a 1200-unit plane asked for a
> 48000×48000 texture.

---

## 4. Render architecture

FFIX **does not draw fields in 3D**. Its Unity camera is **orthographic and essentially 2D**
(`FieldMap.CenterCameraOnPlayer` only moves it in X/Y), and perspective is faked in the vertex
shader of each PSX material through `_MatrixRT` and `_ViewDistance`, emulating the PSX GTE. The
depth that gets written is not a real distance but an OT-style ordering index.

But `BGCAM_DEF` **does store a real 3D camera**: a 3×3 rotation, a translation and `proj` (the
projection distance). A true perspective camera is derived from that.

### The 3D pass

```
FieldMap Camera (orthographic, layer != 30)   <- the game, untouched
Field3D Camera  (derived perspective, layer 30 only, clearFlags=Depth, depth=+1)
  |__ Field3D Root               (identity, field coordinates)
      |__ LIT objects and the player proxy
      |__ Field3D Scene          (SCENESCALE scale, bundle content in metres)
```

The 3D camera is drawn **after** the field, clearing only the z-buffer. Its view and projection
matrices come from `FieldPerspectiveCamera.TryBuildMatrices`.

### Details that took work to find

**The pixel scale is measured, not computed.** The field camera's `aspect` and `pixelRect` change
per map (150 is _pillarboxed_: `x=77.68, width=1764.64` out of 1920). Computing it from
`FieldMap.HalfFieldWidth` gave a horizontal error that grew with distance from the centre. It is
solved by sampling three points with `WorldToScreenPoint` — an orthographic projection is affine, so
three samples determine it exactly.

**The frame offset is a _lens shift_, not a camera movement.** It goes in `P02`/`P12` of the
projection matrix. Moving the camera would change the perspective; the game only shifts the crop.

**The −1 determinant of the view matrix is correct.** `worldToCameraMatrix == Scale(1,1,-1) *
transform.worldToLocalMatrix`, so it is always negative. Forcing it to +1 by mirroring the world
makes the matrix unrepresentable as a transform, and `Quaternion.LookRotation` silently rebuilds the
_right_ axis backwards — which inverts left/right movement while leaving static objects looking
correct.

**Unity culls using the camera's `transform`, not `worldToCameraMatrix`.** Assigning only the matrix
leaves the camera at the origin and everything is discarded before being drawn.

### The character

`FieldPerspectiveCamera.SyncPlayerProxy` takes a snapshot of the deformed meshes every frame with
`SkinnedMeshRenderer.BakeMesh` and copies it into a `MeshRenderer` on layer 30. Modes:

| Mode     | Effect                                                                                                        |
| -------- | ------------------------------------------------------------------------------------------------------------- |
| `off`    | nothing                                                                                                       |
| `shadow` | invisible in the 3D pass but present in the shadow map: the PSX render is still what you see, and it casts a real shadow |
| `full`   | also drawn with `Standard`, on top of the PSX one (useful for comparison)                                     |
| `only`   | turns off the character's PSX renderers: shares a real z-buffer with the 3D geometry                          |

`BakeMesh` **already applies the renderer's scale**, so the proxy runs with `localScale = one`.
Copying the player's `lossyScale` of `(-1,-1,1)` mirrored it and sank it below the floor.

---

## 5. Workflow

### 5.1 Exporting a map

With `EXPORTSCENE` in `MemoriaFieldObjects.txt`, entering the map generates
`<game>/MemoriaSceneExport/<map>/`:

| File             | Contents                                                                       |
| ---------------- | ------------------------------------------------------------------------------ |
| `field.json`     | camera (position, basis, FOV, lens shift), resolution, `sceneScale`            |
| `background.png` | clean background plate, rendered without characters                            |
| `walkmesh.obj`   | collision mesh in field units, with `floorIdx`/`triIdx` in comments            |

It is exported **at runtime and not from the files** because the camera is only determined while
playing: the framing depends on the resolution and on the per-map adjustment.

### 5.2 Generating the Blender project

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe' --background --factory-startup `
  --python DynamicShadows\Tools\blender\build_field_project.py -- `
  "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX\MemoriaSceneExport\150"
```

It produces `field_<map>.blend` with the camera placed, the background as camera layers, the
walkmesh in wireframe, and three reference markers. All in metres.

**The script verifies itself on every run**: it projects the walkmesh vertices with the Blender
camera (`world_to_camera_view`) and compares them against the game's projection. Current state on
map 150: **X 0.063 px, Y 0.037 px**. If it goes above a pixel it says so on screen.

#### Rebuilding the camera: three traps

**1. The exported basis is not orthonormal.** `|up|` is **1.0713**, which is `15/14`: the stretch of
the PSX 320×224 framebuffer shown at 4:3. FFIX carries it inside the camera matrix itself so that
the models line up with the backgrounds, painted for that ratio. A Blender camera is orthonormal by
construction, so the scale is taken out of the basis and moved into the field-of-view tangents:

```
tan_x = tan(fovX/2) · |right| / |forward|
tan_y = tan(fovY/2) · |up|    / |forward|
```

Putting the scale into the columns of `matrix_world` does **not** work: Blender projects with the
true inverse, so it applies it the wrong way round.

**2. The factor is `k/kz`, not `kz/k`.** The game projects with the **inverse** of that basis, and
for orthogonal columns of norm `k` the inverse is the transpose divided by `k²` — not the bare
transpose. Confusing them inverts the factor. The error is hard to see, because if the checking
script makes the same mistake both sides agree while being wrong. What breaks the tie is an
independent quantity: the `pixel aspect` that comes out with the inverse is **0.93359**, and
`(4/3)/(320/224) = 0.93333` is the PSX one. With the transpose you get its reciprocal.

**3. The angular aspect is not the pixel aspect.** 1.5257 against 1.6343. The difference is declared
as pixel aspect, and Blender **only expresses it on the axis that ends up ≥ 1**: setting the other
one below 1 does absolutely nothing. Here it lands on `pixel_aspect_y = 1.07113`,
`pixel_aspect_x = 1.0`.

And the lens shift goes **with the opposite sign** to the game's frame offset. Measured, not
assumed: `d(u)/d(shift_x) = −1` and `d(v)/d(shift_y) = −angular_aspect`.

```
shift_x = −ndcOffsetX / 2
shift_y = −ndcOffsetY / 2 / angular_aspect
```

#### The background

The background is **not geometry**: it is **two camera layers** (`background_images`), under Object
Data Properties > Background Images. Not being a scene object, nothing you model covers or moves
them, and there is no giant plate in the way in the middle of the room.

Each layer is configured the same way:

- `frame_method = STRETCH`, not `FIT`: the image and the frame already share the same ratio, so no
  rounding introduces bands at the sides.
- **offset (0, 0)**: the camera frame already carries the lens shift, so the image matches the
  render with nothing to correct.
- `scale = backgroundScale`: the exporter captures the **whole** background, not only what fits on
  screen. A field background is larger than the window and the game scrolls by moving its
  orthographic camera. The image grows equally on both axes and centred on the frame, which is
  exactly what allows placing it with **a single uniform scale and no offset**.

| Layer               | State    | Use                                                                       |
| ------------------- | -------- | ------------------------------------------------------------------------- |
| `Back`, alpha 1.0   | enabled  | visible wherever nothing has been modelled yet                            |
| `Front`, alpha 0.35 | disabled | enable it and the reference draws **over** the model, for aligning edges  |

> There was an earlier attempt with textured plates —the background as geometry, framed by
> inverting the projection— and with a computed offset on the layers. Both were patches on the
> symptom: the misalignment did not come from the image but from the camera, which had the shift
> sign flipped and a 6.6% scale on Y. With the camera fixed, the offset is unnecessary and so is the
> plate.

### 5.2b Shadows over the background without modelling the scenery

A far cheaper alternative, and the recommended way to start: instead of replacing the background,
you model **very simple geometry** (floor, walls, a column) that **is not drawn** and only serves to
receive the character's shadow and to give real depth.

Two shaders in [DynamicShadows/Unity/DynamicShadows/Assets/Shaders/](DynamicShadows/Unity/DynamicShadows/Assets/Shaders/):

**`Memoria/ShadowCatcher`** — the proxy geometry. The 3D camera only clears the z-buffer, so by the
time it is drawn the framebuffer already holds the prerendered plate. The colour pass uses
`Blend DstColor Zero` and outputs `lerp(shadowColour, white, attenuation)`: where there is no shadow
it multiplies by **1**, and the background is left identical bit for bit, without projecting
textures or matching colour spaces. A first `ColorMask 0` pass in queue `Geometry-1` writes depth
**before** the character, which is what gives real occlusion. It carries no `Fallback`, on purpose:
the geometry does not cast a shadow, because the scenery's shadows are already painted into the
background and casting them again would double them.

**`Memoria/FieldActorLit`** — the character. It reproduces the arithmetic of `PSX/FieldMapActor`,
read off its d3d9 assembly in `StreamingAssets/Shaders/PSX/FieldMapActor.txt`:

```
mad r3, r0.w, v0.w, c1.x    ; texA * colorA - 0.5
texkill r3                  ;   -> clip(c.a - 0.5)
mul_pp r0, r0, v0           ; c = tex * (vertexColour * _Color)
mul_pp r1.xyz, r0.w, r0
add_pp r0.xyz, r1, r1       ; rgb = 2 * c.a * c.rgb     (premultiplied modulate2x)
```

with `Blend One OneMinusSrcAlpha`. With `_LightInfluence = 0` the output is **identical** to the
game's; raising it brings in the directional, the ambient and the point lights of the `ForwardAdd`
pass. Its `ShadowCaster` pass repeats the alpha cutout: without it, hair and capes —which are quads
with a cut-out texture— would cast rectangles.

A shader **cannot be compiled at runtime** (the game's 140 subprograms are precompiled d3d9
assembly), so both travel inside the bundle, compiled by the 5.2.3 editor. The character material is
picked up in [FieldSceneBundle.cs](Assembly-CSharp/Memoria/Field/FieldSceneBundle.cs) `Adopt`, by
looking for any material whose shader is named `Memoria/FieldActorLit`.

The object carrying it has to be left **active with the Mesh Renderer unchecked**, not the other way
round: the scene content is located by walking the root objects with `FindObjectsOfType`, which
**does not return disabled objects**, so a disabled carrier at the root would never be found. With
the renderer unchecked it draws nothing and is still found. This is handled by
[SetupDynamicShadowsScene.cs](DynamicShadows/Unity/DynamicShadows/Assets/Editor/SetupDynamicShadowsScene.cs).
If a shader does not survive packaging it is reported in the log rather than silently drawing pink.

Order of work, each milestone checkable on its own:

| Milestone | Config                                                      | What has to be visible                                                    |
| --------- | ----------------------------------------------------------- | ------------------------------------------------------------------------- |
| 1         | `PLAYER3D shadow` + bundle with a catcher floor and directional | the game intact and **one shadow** of the character on the floor      |
| 2         | add walls and columns to the catcher                        | the shadow climbs the wall                                                |
| 3         | `PLAYER3D only` + `CHARLIGHT 0`                             | **nothing** about the character changes, and it now occludes behind columns |
| 4         | raise `CHARLIGHT` to 0.2–0.4                                | it darkens on entering shadow                                             |
| 5         | point lights in the scene                                   | it takes on a tint when approaching a torch                               |

Milestone 3 is the one to study closely: it is the only moment the character stops being drawn by
the game. If with `CHARLIGHT 0` **any** difference shows, the colour formula is not correctly
reproduced and it has to be fixed before going on.

### 5.2c What took work to find

All of them share one pattern: **a check that looked like it was verifying and was not**. They are
listed here because every one of them can be made again.

**The `BGCAM_DEF` matrix is not a rotation.** Its rows carry scale, and the Y one is `14/15`: the
PSX 320×224 framebuffer shown at 4:3, which FFIX stores there so the models line up with the painted
background. A Unity camera cannot carry it — `Quaternion.LookRotation` silently orthonormalises
whatever it is given — so it has to be **taken out of the view and put into the projection**:
`P00' = P00·kx/kz`, `P11' = P11·ky/kz`, `P23' = P23/kz`. The same mistake was made twice, once on the
Blender camera and once on the game's, months apart conceptually.

> And the diagnostics said `delta=(0.0,0.0)`. It compared the derived projection against
> `PSX.CalculateGTE_RTPT`: **two calculations in C#, neither going through the real camera**. It
> verified the matrices against each other, not what Unity does with them. What exposed it was
> painting the mask in green over the game's render, the first measurement that actually goes
> through the camera.

**Unity does not scale a light's `range` with the transform.** The container multiplies by
`SCENESCALE` to go from metres to field units, but the light's range never finds out: a 3 m torch
ends up reaching 3 units, i.e. 9 millimetres. It is converted when the scene is adopted.

**Removing the `ShadowCaster` pass from a shader stops it RECEIVING shadow.** Directional shadows in
forward rendering are screen-space and are resolved by reading `_CameraDepthTexture`, which Unity
builds from each object's ShadowCaster pass. Without it the catcher never enters the depth texture
and its pixel queries the shadow at the background's depth: always lit. To stop it **casting**, the
place is the _Cast Shadows_ dropdown on the MeshRenderer, not removing the pass.

**Stencil discard was worse than the problem it fixed.** It cut without looking at depth, so it bit
into the character's own shadow where their silhouette touched it. The depth mask is enough and is
correct: it discards only what is behind. If something really is in front, the game is painting the
background there, not the character, and darkening it is the right thing to do.

**Modulating the character's pixels by blending over the game's render cannot work.** The proxy
multiplies whatever it FINDS, and does not know whether the game drew the character there or an NPC
walking in front: with a moogle in the way, a dark ghost of Steiner appeared on top. Lighting goes
through the game material's `_Color` instead (see `CHARLIGHT`).

**`LateUpdate` is not late enough.** Ordering between MonoBehaviours is undefined, so an actor whose
animation the game advances in its own `LateUpdate` ends up posed after ours. It only shows on
things that move fast — a moogle's pompom, not a standing Steiner. The bake goes in the 3D camera's
**`OnPreCull`**, the last instant before drawing.

**And `BakeMesh` applies the renderer's WORLD scale**, not its local one. `localScale = 1` on the
proxy is always correct. "Fixing" it with `lossy/local` mirrors the mesh a second time and flattens
every character — the `local (1,1,1)` next to the `lossy (-1,-1,1)` in the log says so at a glance.

**Direct3D 9 will not take the point-light shadow variants.** The game runs on d3d9, where Unity
compiles to shader model 2.0 unless `#pragma target 3.0` is asked for — and even then,
`multi_compile_fwdadd_fullshadows` generates the cube-map variants (point light shadows), which do
not fit. The whole shader then falls back to the fallback SubShader, silently: `isSupported` is still
`true`. It is fixed with `#pragma skip_variants SHADOWS_CUBE POINT_COOKIE`, which keeps **spotlight**
shadows and discards only the point-light ones.

> And to know which SubShader is active you have to look at `Material.passCount`, which returns the
> ACTIVE one's. Without that, "the spotlights do not cast" is indistinguishable from "the pass did
> not compile", and the second can only be settled by guessing. The loader now says so when adopting
> the scene.

**`scene.new(LINK_COPY)` does not share: it copies the object list as of that instant.** A field with
two BGCAMs needs two Blender scenes, because resolution and pixel aspect belong to the SCENE. With
the linked copy, anything modelled afterwards appeared **only in the active scene** —precisely what
it was supposed to solve— and the second scene also inherited the first one's `BackgroundPlate`, a
huge plate with the background painted on it in the middle of the room. What actually shares is a
**collection**: `Scenery` linked into every scene, plus one collection per camera with its camera and
its background, linked only into its own.

> This was found by opening the generated `.blend` and listing which objects each scene has, plus a
> test cube to see where it landed. **None of it shows by looking at the file in Blender**: both
> scenes look fine when freshly generated, and the fault only appears once you start modelling.

**Restoring a value that was automatic makes it manual.** `Camera.aspect` derives itself from the
viewport **until it is assigned**; from then on it is pinned. The background exporter opens the
viewport for the capture and then "restored" with `camera.aspect = previousAspect`, which restores
nothing: it pins whatever value was current at that instant. And the instant matters, because the
game narrows the field viewport a frame after entering. The first visit to a map exports with the
viewport already narrow and pins the right value **by luck**; coming back, it exports one frame
earlier, full screen, and pins 16:9 forever. The field is then drawn with a horizontal scale that is
not its viewport's and the proxy stops lining up. The correct call is `camera.ResetAspect()` when it
was deriving itself.

> The log had it in plain sight: `CAMERA ortho ... aspect=1.778 pixelRect=(x:77.68, width:1764.64
> ...)`. 1764.64/1080 = **1.634**, not 1.778. **A diagnostic that prints two figures which have to
> agree is worth more than one that prints the conclusion**, because the conclusion comes out right
> even when the system is wrong — the `delta=(0.0,0.0)` right next to it kept saying everything
> agreed.
>
> And that same haste was breaking the export: on map 150, on returning, the background came out at
> 1920x1080 with fovX 47.83 instead of 1765x1080 with 44.36. The Blender project was left with a
> camera that is not the game's, with nothing warning about it. It now waits for the viewport to
> hold still for three frames.

**`FindObjectsOfType` does not see what is disabled, and that turns a diff into a trap.** What gets
adopted from a bundle is the difference between the roots before the additive load and those after.
If the BEFORE snapshot is taken with `FindObjectsOfType`, a game object that happened to be off at
that instant is missing from it, and when the game switches it on a few frames later it looks "new
since the load": the 3D pass takes it, reparented and moved to another layer. **It only fails on
returning to a map**, because on the first visit there are barely any disabled objects. The before
snapshot uses `Resources.FindObjectsOfTypeAll`; the after one does NOT, because that also returns
loaded assets.

> Over-collecting in the before snapshot is free —at worst it leaves something unadopted, and that is
> visible. Over-collecting in the after one is what breaks. **The two snapshots of a diff do not have
> to be taken the same way: each has its own safe side to err on.**

**And stopping the adoption at the first frame that yields something is betting that the additive
load hands over all its roots at once.** It does not guarantee that. Whatever arrives later stays
outside the container: without the `SCENESCALE` scale and on the layer the 3D camera does not draw,
i.e. invisible and silent.

**Cleanup cannot live only in the exit hook.** Hanging it off `ff9ShutdownStateFieldMap` makes it
depend on that path always being taken —battle, menu, FMV, returning to the same map. Entering a map
now cleans up too, and warns if it finds anything alive from the previous one. A proxy that outlives
its map is one silhouette too many in the depth mask.

**A Unity light's falloff is not "how much light arrives".** The catcher darkens by
`reach × (1 − shadow)`, and reach was coming from `UnitySpotAttenuate`, which is **only the distance
falloff**. That falloff is brutal — at half the range it is already down to 13% — so a spotlight of
intensity 3.5 that lights a `Standard` plane perfectly well gave a factor of 8% here: an 8% shadow
over a prerendered background is invisible. What arrives is **falloff × intensity**, and that is
`_LightColor0.rgb`, which already carries colour multiplied by intensity.

> It took three attempts because everything that was checked was fine: Unity was generating the
> shadow map (proved by putting a `PRIMITIVE_PLANE … LIT` with `Standard` next to it, which did
> receive the shadow), the shader compiled in full (`passCount` = 4) and read the map correctly. **A
> plate of a different material next to the one that fails is the cheapest discriminator there is**:
> it separates "Unity is not doing it" from "my shader is not picking it up" in a single screenshot.
>
> And what closed the case was splitting the factor into its two terms and painting each one alone in
> black and white (`CATCHERDEBUG 2` and `3`). Looking at the final result only says "nothing shows",
> which is compatible with five different causes. **When a product comes out wrong, look at the
> factors, not at the product.**

#### The diagnostic tools that remain

|                     | What it measures                                                                                                                                                                                |
| ------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MASKDEBUG on`      | paints the proxy green over the game's render. The only thing that goes through the real camera                                                                                                  |
| `CATCHERDEBUG 1..4` | isolates each term of the catcher's additive pass: 1 the whole pass in red, 2 the shadow, 3 the reach, 4 the final factor. The mode lives in the txt, so it changes without rebuilding the bundle |
| `CAMERA`            | projection error per actor **and at the mesh centre**, which is what catches a scale error: the origin can be pinned while the body is not                                                       |
| adoption log        | colliders, converted light ranges, **which SubShader ended up active**, unsupported shaders, static batching                                                                                     |

The lesson, in one line: **a check that does not go through the real system is not a check.**

### 5.3 Modelling and taking it into Unity

You model over the real background and walkmesh. Then, in Unity 5.2.3:

1. Import the FBX and place it **without moving it** (the positions are already correct)
2. Lights, `Lightmap Static` on the geometry, `Baked GI` on and `Precomputed Realtime GI` off
3. `Window > Lighting > Build`
4. `Dynamic Shadows > Build Bundle` (menu from [BuildSceneBundle.cs](DynamicShadows/Unity/DynamicShadows/Assets/Editor/BuildSceneBundle.cs)),
   which writes the `.unity3d` straight into `DynamicShadows/Mod/DynamicShadows/`
5. Deploy and **restart the game**

---

## 6. `MemoriaFieldObjects.txt` reference

It ships inside the mod and is re-read **on every field load**. A copy in the game root takes
priority over the mod's. Changing positions needs no rebuild: leave the map and walk back in.

### Objects

```
<fldMapNo> <model> <x> <y> <z> [scale] [LIT]
```

- `LIT` → drawn by the 3D camera with the `Standard` shader, with light and shadows. Without `LIT`
  it uses the game's PSX projection.
- Models: a registered GEO name, `PRIMITIVE_CUBE` or `PRIMITIVE_PLANE`.
- A `@` in front of the X makes the coordinates relative to `bgi.charPos`. **Careful**: that is _not_
  the entry point (on map 150 it is `(-1423, 0, 1347)` while you walk in Z 23..430).

### Global settings

| Line                                            | Effect                                                              |
| ----------------------------------------------- | ------------------------------------------------------------------- |
| `SCENESCALE <factor>`                           | field units per scene unit (345)                                    |
| `SCENEBUNDLE auto`                              | loads `<fldMapNo>.unity3d` on every map that has one                |
| `SCENEBUNDLE <map> <file> [scene]`              | loads one specific Unity scene bundle                               |
| `AMBIENT <r> <g> <b> [intensity]`               | ambient light of the 3D pass, 0–1 per channel                       |
| `LIGHT <eulerX> <eulerY> <eulerZ> [intensity]`  | directional created in code; **do not use if the bundle brings one** |
| `SHADOWDISTANCE auto \| <units>`                | `auto` measures it per map; the default 40 is useless at field scale |
| `CHARLIGHT <gain>`                              | how much of the scene's light reaches the characters                |
| `PLAYER3D off\|shadow\|full\|only`              | character proxy mode                                                |

### Diagnostics

| Line                | Effect                                                                        |
| ------------------- | ----------------------------------------------------------------------------- |
| `TRACE`             | player position in `Memoria.log` while walking, in field and scene units      |
| `CAMERA`            | compares the PSX projection with the derived camera's, and reports on the proxy |
| `DUMP`              | renderers and materials of what was spawned and of the player                 |
| `MASKDEBUG [on\|off]` | paints the character proxy green over the game's render                     |
| `CATCHERDEBUG 1..4` | isolates each term of the catcher's additive light pass                       |
| `PROBE`             | shaders and shadow capabilities that survived the build's stripping           |
| `EXPORTSCENE`       | dumps the current map (§5.1)                                                  |

---

## 7. Known traps

Each of these cost at least one iteration. They are ordered by how likely they are to come back.

**A material with no texture = invisible (via PSX).** `PSX/FieldMapActor` discards every pixel where
`textureAlpha * vertexColourAlpha <= 0.5`. With no texture, Unity uses the one the shader declares
(`"grey"`), whose alpha is not 1, and the whole model disappears. The code assigns an emergency white
texture. _Does not apply to materials that come inside a bundle._

**`Batching Static` breaks runtime scaling.** Unity precombines the meshes at build time with their
transform baked into the vertices, and the renderer ignores the transform afterwards. Symptom:
`SCENESCALE` has no effect and the scene renders at metric size. Fix:
`Edit > Project Settings > Player > Rendering` → untick **Static Batching**. The loader detects it
and warns.

**Field scale breaks every "per unit" Unity setting.** It has already bitten us with
`shadowDistance` (40 by default) and with `Baked Resolution`. It will show up again with particle
sizes, LOD and physics. Rule of thumb: if a setting comes in units, it was designed for metres.

**`AssetBundle.CreateFromFile` only opens uncompressed bundles.**
`BuildStreamedSceneAssetBundle` compresses by default (`UnityWeb`, LZMA). The editor script passes
`BuildOptions.UncompressedAssetBundle` and the loader has a fallback using
`CreateFromMemoryImmediate`. The `.unity3d` header carries the Unity version in plain text — useful
for diagnosis.

**Regenerating the bundle requires restarting the game.** Bundles stay open for the whole session
because `CreateFromFile` fails when opening the same file twice.

**Close Blender before regenerating the `.blend`.** Blender does not lock the file; the open instance
keeps the old version in memory and writes it over yours on save.

**The deploy script does not put `MemoriaFieldObjects.txt` in the game root** unless `-EditConfig` is
passed, and if one is already there it warns that it takes priority over the mod's. That is
deliberate — it is what allows tuning positions in game — but it explains several "it does not work"
reports that were really stale configuration.

**`bgi.charPos` is not the entry point.** It is the default position `FieldMap.AddPlayer` uses only
in debug mode. For useful coordinates, use `TRACE`.

**The character's bounds are inflated on purpose.** `FieldMapActor` sets them to
`Single.MaxValue * 0.01f` to disable culling, because the PSX projection happens in the vertex
shader. Any diagnostic based on the player's `renderer.bounds` gives garbage.

**Comparing heights by eye does not work.** In a 3/4 top-down view, depth translates into screen
height: a more distant object is drawn higher up and looks taller. That is why the scale factor came
from the game's own table and not from perception.

**A 180° rotation is invisible.** Unlike a mirror, it leaves the scene looking normal. It can only be
detected by measuring the full round trip back into the game.

---

## 8. Verification tools

| Tool                                                            | Use                                                                                                                                     |
| --------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| [check_export.py](DynamicShadows/Tools/blender/check_export.py)  | checks an export **without opening Blender**: projects the walkmesh via the game's route and via Blender's and reports the pixel deviation |
| [dump_fbx.py](DynamicShadows/Tools/dump_fbx.py)                  | validates an FBX against what Memoria's importer requires (mandatory material, UVs, `Lcl Scaling`)                                      |
| [make_cube_fbx.py](DynamicShadows/Tools/make_cube_fbx.py)        | generates a test FBX with headless Blender                                                                                              |
| `CAMERA` in the `.txt`                                           | continuous verification in game: `delta` must be `0.00`                                                                                 |

**Verifications passed**, in case they have to be redone after a large change:

```
field coordinates -> game camera          delta 0.00 px
    (3 projection distances, 2 cameras, narrow and wide maps, scrolling in X and Y)
field coordinates -> Blender project      max deviation 0.00 px
Blender -> FBX -> Unity -> game           3 markers on different axes, exact
```

---

## 9. Status and remaining work

### Done

- Perspective camera derived from the field, pixel exact
- Real directional light and cast shadows
- Character in the 3D pass with correct shadow and depth
- Unity scenes loaded with baked lightmaps
- Map exporter and Blender project generator
- Calibrated scale and a coordinate chain verified end to end

### Milestone 4 — replacing the background

For when there is real geometry to show. No technical unknowns:

1. Stop drawing `BGSCENE_DEF` and its overlays
2. Change the 3D camera's `clearFlags` from `Depth` to `SolidColor` or `Skybox`
3. Remove the fake shadow in `FieldMapActor.CreateShadowMesh`, which becomes redundant

### Open questions

**The game's VFX.** FFIX's torches are frame animations of the background (`BGANIM_DEF`, `EBG_anim*`
opcodes) and **are lost when it is replaced**: they have to be redone in the 3D scene. SPS effects
(`SPSEffect`, smoke, magic, rain) do survive because they are separate objects, but they are drawn in
the PSX pass with fake depth and would composite badly against 3D geometry: they would have to be
routed into the 3D pass the way the character was.

**Light probes.** `probes: 0` in every test. Without them the character only receives the directional
and the ambient, and does not react to the scenery's local lights. An alternative without probes:
split into two layers —static scenery and character— and use realtime point lights with `cullingMask`
restricted to the character's layer.

**Particles.** The `ParticleSystem` modules (`emission`, `shape`, `colorOverLifetime`,
`textureSheetAnimation`) **are not script-accessible in Unity 5.2** — they arrived in 5.3. Only
top-level properties exist. A decent particle system is editor content. On top of that it has not
been checked that the `Particles/*` shaders survived stripping: add them to `PROBE` before counting
on them.

**Custom shaders.** Unity **does not compile Cg/HLSL at runtime**: `ShadersLoader` uses
`new Material(shaderCode)` and the repo's 140 subprograms are `d3d9` assembly. A new shader would
have to be written that way. The built-in `Standard`, `Diffuse`, `Legacy Shaders/Diffuse`,
`VertexLit`, `Mobile/VertexLit` and `Unlit/Transparent Cutout` **did** survive stripping and carry a
ShadowCaster; `Bumped Diffuse`, `Mobile/Diffuse`, `Transparent/Cutout/Diffuse` and `Unlit/Texture`
did not.

---

## 10. File map

### Engine code (`Assembly-CSharp/Memoria/Field/`)

| File                        | Responsibility                                                       |
| --------------------------- | -------------------------------------------------------------------- |
| `FieldPerspectiveCamera.cs` | camera derivation, 3D pass, character proxy, light and ambient       |
| `CustomFieldObjects.cs`     | reading `MemoriaFieldObjects.txt`, spawning objects, diagnostics     |
| `FieldSceneBundle.cs`       | loading Unity scene bundles and adopting them into the 3D pass       |
| `FieldSceneExport.cs`       | exporting camera, background and walkmesh                            |

Hook points in the game, all in `HonoluluFieldMain`: `ff9InitStateFieldMap` (spawn on map load),
`HonoUpdate` (per-frame sync), `HonoLateUpdate` and `ff9ShutdownStateFieldMap` (cleanup).

### Tools

| File                                                   | Use                                            |
| ------------------------------------------------------ | ---------------------------------------------- |
| `DynamicShadows/Tools/build-and-deploy.ps1`            | build and deploy                               |
| `DynamicShadows/Tools/blender/build_field_project.py`  | generate a map's Blender project               |
| `DynamicShadows/Tools/blender/update_field_project.py` | refresh an existing project without losing work |
| `DynamicShadows/Tools/blender/check_export.py`         | verify an export without Blender               |
| `DynamicShadows/Unity/.../Assets/Editor/`              | the `Dynamic Shadows >` menus in the Unity editor |
| `DynamicShadows/Tools/dump_fbx.py`, `make_cube_fbx.py` | FBX utilities                                  |

### Data

- `DynamicShadows/Mod/DynamicShadows/` — the mod exactly as installed: `ModDescription.xml`,
  `MemoriaFieldObjects.txt`, `DictionaryPatch.txt`, the bundles and the assets
- `<game>/MemoriaFieldObjects.txt` — optional live override, takes priority over the mod's
- `<game>/MemoriaSceneExport/<map>/` — exports
- `<game>/Memoria.log` — log; recreated on every launch

---

## 11. A note on method

The pattern that has worked, and is worth keeping: **when something is not visible, do not guess.**
Add a diagnostic that prints the figure separating the hypotheses, and decide with the number.
Several of this project's bugs —the left/right inversion, the static batching, the 180° rotation—
were invisible to the naked eye and only fell once measured.

And the other way round: two of the early diagnostics were **false positives** that cost iterations
—the vertex colours and the determinant of the view matrix. It is worth confirming a hypothesis
before building on it.
