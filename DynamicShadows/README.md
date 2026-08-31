# Dynamic Shadows — escenarios 3D en Final Fantasy IX sobre Memoria

Documento de traspaso. Describe el objetivo, lo que está construido y verificado, el flujo de
trabajo, y las trampas que ya nos han costado tiempo. Todo lo que aquí se afirma como "verificado"
se comprobó con números durante el desarrollo, no a ojo.

---

## 0. Cómo está organizado el repo

Este repo **es** el fork de Memoria: el pase 3D no puede ser un mod normal, porque el sistema de
mods de Memoria carga datos y no código. Así que el motor y el contenido van juntos, pero
separados en el árbol:

```
Assembly-CSharp/Memoria/Field/     el código del pase 3D (lo que se compila en el DLL)
  CustomFieldObjects.cs            configuración por mapa, spawn de objetos, diagnóstico
  FieldPerspectiveCamera.cs        cámara derivada de BGCAM_DEF, sombras, proxy del personaje
  FieldSceneBundle.cs              carga del bundle de Unity de cada mapa
  FieldSceneExport.cs              volcado de un mapa para Blender (EXPORTSCENE)

DynamicShadows/                    todo lo que no es código del motor
  README.md                        este documento
  Mod/DynamicShadows/              el mod tal y como se instala en el juego
  Unity/DynamicShadows/            proyecto de Unity 5.2.3f1 donde se iluminan las escenas
  Tools/                           build, generadores de Blender y utilidades
```

Aparte de esos cuatro ficheros, el fork solo toca **9 líneas** de Memoria: cinco *hooks* en
`Global/Honolulu/HonoluluFieldMain.cs` y cuatro `<Compile Include>` en el `.csproj`. Mantenerlo
así de pequeño es deliberado: es lo que permite rebasar sobre `upstream/main` sin dolor.

### Instalar

1. `.\DynamicShadows\Tools\build-and-deploy.ps1` (PowerShell **como administrador**: el juego
   está en Program Files). Compila el DLL, lo copia a `x64\FF9_Data\Managed\` y despliega
   `Mod/DynamicShadows/` en la raíz del juego.
2. Activar **Dynamic Shadows** en el Mod Manager del launcher, o añadirlo a mano en
   `Memoria.ini`, sección `[Mod]`, `FolderNames`. El script avisa si falta.

El mod trae su propio `MemoriaFieldObjects.txt`. Una copia en la raíz del juego tiene prioridad
sobre la del mod y se relee al cargar cada mapa: es la vía para ajustar posiciones, luces y
`CHARLIGHT` en caliente sin redesplegar. `-EditConfig` la deja puesta.

> **Lo que impide distribuirlo como un mod normal.** El Mod Manager instala carpetas de datos;
> no carga ensamblados. El pase 3D vive en `Assembly-CSharp.dll`, así que un release tiene que
> traer el DLL y es incompatible con cualquier otro mod que también lo reemplace. La salida
> limpia es que el código acabe *dentro* de Memoria vía PR upstream: entonces este mod pasa a ser
> solo datos y deja de tener ese conflicto.

---

## 1. Objetivo

Sustituir los fondos prerenderizados de FFIX por escenarios 3D reales, modelados en Blender e
iluminados en Unity, con el personaje integrado: iluminado por las mismas luces, proyectando sombra
sobre la geometría y ocluyéndose correctamente con ella.

El mapa de pruebas es **150 — `Cast. Alex./Guardia`** (cuartel de la guardia del Castillo de
Alexandria), pequeño y con un save de Steiner disponible.

---

## 2. Entorno

| Pieza        | Detalle                                                                        |
| ------------ | ------------------------------------------------------------------------------ |
| Juego        | FF9 de Steam, `C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX` |
| Motor        | **Unity 5.2.3f1** (según `FileVersion` de `x64\FF9.exe`)                       |
| Memoria      | este repo: fork de `Albeoris/Memoria`, rama `dynamic-shadows`                   |
| Unity Editor | 5.2.3f1, proyecto en `DynamicShadows/Unity/DynamicShadows/`                    |
| Blender      | 5.1 en `C:\Program Files\Blender Foundation\Blender 5.1`                       |

### Compilación

Memoria **no es un plugin**: es el propio `Assembly-CSharp.dll` del juego reescrito. No hay Harmony
ni BepInEx en el repo; los métodos se editan directamente en el fuente decompilado.

```powershell
.\DynamicShadows\Tools\build-and-deploy.ps1              # compila y despliega
.\DynamicShadows\Tools\build-and-deploy.ps1 -EditConfig  # además saca la config a la raíz
.\DynamicShadows\Tools\build-and-deploy.ps1 -SkipBuild   # solo despliega el mod
```

Dos particularidades del entorno, ya resueltas dentro del script:

- Se usa el **MSBuild de VS 2022 Build Tools**, no el de VS 2026: los proyectos C++
  (`SaXAudio`, `Memoria.Injection`) piden el toolset `v143`, y VS 2026 solo trae `v145`.
- Hace falta `-p:FrameworkPathOverride=<repo>\References\` porque
  `Memoria.XInputDotNetPure.csproj` es el único proyecto v3.5 sin esa propiedad, y no hay
  targeting pack de .NET 3.5 instalado.

---

## 3. Los tres sistemas de coordenadas

Esto es el corazón de todo. **Manda el juego**; Blender y Unity son vistas suyas.

### Espacio de campo (autoritativo)

Unidades internas de FFIX. **+Y es arriba** (lo confirma `FieldMap.charAimHeight`, que se _suma_
para elevar el punto de mira de la cámara, y `PSX.CalculateGTE_RTPT`, que niega la Y precisamente
para convertir a la convención Y-abajo de PSX).

**Escala: 345 unidades de campo por metro.** No es una estimación: sale de
`FF9BattleDBHeightAndRadius`, que da la altura de cada modelo. Steiner (`GEO_MAIN_F0_STN`, GEO id 5489) mide **603 unidades**:

```
factor = 603 / altura_en_metros    ->    603 / 1.75 = 345
```

### La base de la cámara lleva escala

`BGCAM_DEF` no guarda una rotación pura. La base que exporta `field.json` es ortogonal pero **no
ortonormal**: `|right| ≈ 1`, `|forward| ≈ 1`, pero **`|up| = 1.0713 = 15/14`**, el estiramiento de
320×224 a 4:3 de PSX. Cualquier cosa que reconstruya esta cámara tiene que descomponerla en
rotación × escala y llevar la escala al campo de visión, **y usar la inversa, no la traspuesta**.
Ver §5.2.

### Espacio de Blender

`campo → blender:  (-x, -z, y) / 345`

La permutación Y↔Z es el cambio de quiralidad (campo es zurdo con Y arriba, Blender diestro con Z
arriba). Las **negaciones de X y Z compensan una rotación de 180° sobre el eje vertical que
introduce la cadena de exportación FBX**, medida con marcadores (§7).

### Espacio de Unity

Se modela en **metros**, y el runtime multiplica por `SCENESCALE` al cargar. Los objetos van bajo
un contenedor `Field3D Scene` con esa escala.

> **Por qué métrico y no unidades de campo:** la escala de campo rompe todos los valores por
> defecto de Unity que van "por unidad". El horneado de lightmaps con `Baked Resolution` = 40
> téxeles/unidad sobre un plano de 1200 unidades pedía una textura de 48000×48000.

---

## 4. Arquitectura del render

FFIX **no dibuja los fields en 3D**. Su cámara de Unity es **ortográfica y esencialmente 2D**
(`FieldMap.CenterCameraOnPlayer` solo la mueve en X/Y), y la perspectiva se falsifica en el vertex
shader de cada material PSX mediante `_MatrixRT` y `_ViewDistance`, emulando el GTE de la PSX. El
depth que se escribe no es distancia real, sino un índice de orden tipo OT.

Pero `BGCAM_DEF` **sí guarda una cámara 3D de verdad**: rotación 3×3, traslación y `proj`
(distancia de proyección). De ahí se deriva una cámara en perspectiva real.

### El pase 3D

```
FieldMap Camera (ortográfica, capa != 30)   ← el juego, tal cual
Field3D Camera  (perspectiva derivada, solo capa 30, clearFlags=Depth, depth=+1)
  └─ Field3D Root                (identidad, coordenadas de campo)
     ├─ objetos LIT y proxy del jugador
     └─ Field3D Scene            (escala SCENESCALE, contenido del bundle en metros)
```

La cámara 3D se dibuja **después** del field, borrando solo el z-buffer. Su matriz de vista y su
proyección salen de `FieldPerspectiveCamera.TryBuildMatrices`.

### Detalles que costaron encontrar

**La escala en píxeles se mide, no se calcula.** El `aspect` y el `pixelRect` de la cámara del
field cambian por mapa (el 150 está _pillarboxed_: `x=77.68, width=1764.64` sobre 1920). Calcularlo
desde `FieldMap.HalfFieldWidth` daba un error horizontal creciente con la distancia al centro. Se
resuelve muestreando tres puntos con `WorldToScreenPoint` — una proyección ortográfica es afín, así
que tres muestras la determinan exactamente.

**El desplazamiento de encuadre es un _lens shift_, no un movimiento de cámara.** Va en `P02`/`P12`
de la matriz de proyección. Mover la cámara cambiaría la perspectiva; el juego solo desplaza el
recorte.

**El determinante −1 de la matriz de vista es correcto.** `worldToCameraMatrix == Scale(1,1,-1) *
transform.worldToLocalMatrix`, así que siempre es negativo. Forzarlo a +1 espejando el mundo hace
la matriz irrepresentable como transform, y `Quaternion.LookRotation` reconstruye el eje _right_ al
revés en silencio — lo que invierte el movimiento izquierda/derecha dejando los objetos estáticos
con aspecto correcto.

**Unity culla con el `transform` de la cámara, no con `worldToCameraMatrix`.** Asignar solo la
matriz deja la cámara en el origen y se descarta todo antes de dibujarlo.

### El personaje

`FieldPerspectiveCamera.SyncPlayerProxy` toma cada frame una instantánea de las mallas deformadas
con `SkinnedMeshRenderer.BakeMesh` y la copia a `MeshRenderer` en la capa 30. Modos:

| Modo     | Efecto                                                                                                       |
| -------- | ------------------------------------------------------------------------------------------------------------ |
| `off`    | nada                                                                                                         |
| `shadow` | invisible en el pase 3D pero presente en el shadow map: se sigue viendo el render PSX y proyecta sombra real |
| `full`   | además se dibuja con `Standard`, encima del PSX (útil para comparar)                                         |
| `only`   | apaga los renderers PSX del personaje: comparte z-buffer real con la geometría 3D                            |

`BakeMesh` **ya aplica la escala del renderer**, así que el proxy va con `localScale = one`. Copiar
el `lossyScale` del jugador `(-1,-1,1)` lo espejaba y lo hundía bajo el suelo.

---

## 5. Flujo de trabajo

### 5.1 Exportar un mapa

Con `EXPORTSCENE` en `MemoriaFieldObjects.txt`, entrar al mapa genera
`<juego>/MemoriaSceneExport/<mapa>/`:

| Archivo          | Contenido                                                                      |
| ---------------- | ------------------------------------------------------------------------------ |
| `field.json`     | cámara (posición, base, FOV, lens shift), resolución, `sceneScale`             |
| `background.png` | placa limpia del fondo, renderizada sin personajes                             |
| `walkmesh.obj`   | malla de colisión en unidades de campo, con `floorIdx`/`triIdx` en comentarios |

Se exporta **en runtime y no de los archivos** porque la cámara solo queda determinada al jugar: el
encuadre depende de la resolución y del ajuste por mapa.

### 5.2 Generar el proyecto de Blender

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.1\blender.exe' --background --factory-startup `
  --python tools\blender\build_field_project.py -- `
  "C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX\MemoriaSceneExport\150"
```

Produce `field_<mapa>.blend` con la cámara colocada, el fondo como capas de la cámara, el walkmesh
en wireframe, y tres marcadores de referencia. Todo en metros.

**El script se verifica solo en cada ejecución**: proyecta los vértices del walkmesh con la cámara
de Blender (`world_to_camera_view`) y los compara con la proyección del juego. Estado actual del
mapa 150: **X 0.063 px, Y 0.037 px**. Si sube de un píxel lo dice en pantalla.

#### Reconstruir la cámara: tres trampas

**1. La base exportada no es ortonormal.** `|up|` vale **1.0713**, que es `15/14`: el estiramiento
del framebuffer de 320×224 de PSX mostrado en 4:3. FFIX lo lleva dentro de la propia matriz de
cámara para que los modelos casen con los fondos, pintados para esa proporción. Una cámara de
Blender es ortonormal por construcción, así que la escala se saca de la base y pasa a las
tangentes del campo de visión:

```
tan_x = tan(fovX/2) · |right| / |forward|
tan_y = tan(fovY/2) · |up|    / |forward|
```

Meter la escala como columnas de `matrix_world` **no** funciona: Blender proyecta con la inversa
de verdad, así que la aplica al revés.

**2. El factor es `k/kz`, no `kz/k`.** El juego proyecta con la **inversa** de esa base, y para
columnas ortogonales de norma `k` la inversa es la traspuesta dividida por `k²` — no la traspuesta
a secas. Confundirlas invierte el factor. El error es difícil de ver porque si el script de
comprobación comete el mismo, los dos lados cuadran estando mal. Lo que lo desempata es una
magnitud independiente: el `pixel aspect` que sale con la inversa es **0.93359**, y
`(4/3)/(320/224) = 0.93333` es el de PSX. Con la traspuesta sale su recíproco.

**3. El aspecto angular no es el de píxeles.** 1.5257 contra 1.6343. La diferencia se declara como
pixel aspect, y Blender **solo lo expresa en el eje que queda ≥ 1**: poner el otro por debajo de 1
no hace absolutamente nada. Aquí toca `pixel_aspect_y = 1.07113`, `pixel_aspect_x = 1.0`.

Y el lens shift va **con el signo contrario** al desplazamiento de encuadre del juego. Medido, no
supuesto: `d(u)/d(shift_x) = −1` y `d(v)/d(shift_y) = −aspecto_angular`.

```
shift_x = −ndcOffsetX / 2
shift_y = −ndcOffsetY / 2 / aspecto_angular
```

#### El fondo

El fondo **no es geometría**: son **dos capas de la cámara** (`background_images`), en Object Data
Properties > Background Images. Al no ser un objeto de la escena, nada de lo que modeles las tapa
ni las mueve, y no hay un plano gigante estorbando en mitad de la sala.

Cada capa se configura igual:

- `frame_method = STRETCH`, no `FIT`: la imagen y el encuadre ya tienen la misma proporción, y así
  ningún redondeo mete bandas por los lados.
- **offset (0, 0)**: el encuadre de la cámara ya lleva el lens shift, así que la imagen coincide
  con el render sin corregir nada.
- `scale = backgroundScale`: el exportador captura el fondo **entero**, no solo lo que cabe en
  pantalla. Un fondo de field es mayor que la ventana y el juego hace scroll moviendo su cámara
  ortográfica. La imagen crece por igual en los dos ejes y centrada en el encuadre, que es justo
  lo que permite colocarla con **una sola escala uniforme y sin desplazamiento**.

| Capa                | Estado      | Uso                                                                                |
| ------------------- | ----------- | ---------------------------------------------------------------------------------- |
| `Back`, alpha 1.0   | activa      | se ve donde aún no hay nada modelado                                               |
| `Front`, alpha 0.35 | desactivada | actívala y la referencia se dibuja **por encima** del modelo, para alinear aristas |

> Hubo un intento anterior con planos texturizados —el fondo como geometría, encuadrado invirtiendo
> la proyección— y con offset calculado en las capas. Las dos cosas eran un parche sobre el
> síntoma: el desfase no venía de la imagen, sino de la cámara, que tenía el shift con el signo
> cambiado y un 6.6% de escala en Y. Arreglada la cámara, sobra el offset y sobra el plano.

### 5.2b Sombras sobre el fondo sin modelar el escenario

Alternativa de mucho menos trabajo, y el camino recomendado para empezar: en vez de sustituir el
fondo, se modela **geometría muy simple** (suelo, paredes, una columna) que **no se dibuja** y solo
sirve para recibir la sombra del personaje y para dar profundidad real.

Dos shaders en [DynamicShadows/Unity/DynamicShadows/Assets/Shaders/](DynamicShadows/Unity/DynamicShadows/Assets/Shaders/):

**`Memoria/ShadowCatcher`** — la geometría proxy. La cámara 3D solo limpia el z-buffer, así que al
dibujarse el framebuffer ya contiene la placa prerenderizada. El pase de color usa
`Blend DstColor Zero` y saca `lerp(colorSombra, blanco, atenuación)`: donde no hay sombra multiplica
por **1**, y el fondo queda idéntico bit a bit, sin proyectar texturas ni casar espacios de color.
Un primer pase `ColorMask 0` en cola `Geometry-1` escribe la profundidad **antes** que el personaje,
que es lo que le da oclusión de verdad. No lleva `Fallback`, a propósito: la geometría no proyecta
sombra, porque las del escenario ya están pintadas en el fondo y volverlas a proyectar las
duplicaría.

**`Memoria/FieldActorLit`** — el personaje. Reproduce la aritmética de `PSX/FieldMapActor`, leída de
su ensamblador d3d9 en `StreamingAssets/Shaders/PSX/FieldMapActor.txt`:

```
mad r3, r0.w, v0.w, c1.x    ; texA * colorA - 0.5
texkill r3                  ;   -> clip(c.a - 0.5)
mul_pp r0, r0, v0           ; c = tex * (colorVertice * _Color)
mul_pp r1.xyz, r0.w, r0
add_pp r0.xyz, r1, r1       ; rgb = 2 * c.a * c.rgb     (modulate2x premultiplicado)
```

con `Blend One OneMinusSrcAlpha`. Con `_LightInfluence = 0` la salida es **idéntica** a la del
juego; subiéndolo entra la direccional, la ambiental y las luces puntuales del pase `ForwardAdd`.
Su pase `ShadowCaster` repite el recorte alfa: sin él, el pelo y las capas —que son quads con
textura calada— proyectarían rectángulos.

Un shader **no se puede compilar en runtime** (los 140 subprogramas del juego son ensamblador d3d9
precompilado), así que ambos viajan dentro del bundle, compilados por el editor 5.2.3. El material
del personaje se recoge en [FieldSceneBundle.cs](Assembly-CSharp/Memoria/Field/FieldSceneBundle.cs)
`Adopt`, buscando cualquier material cuyo shader se llame `Memoria/FieldActorLit`.

El objeto que lo lleve tiene que quedarse **activo con el Mesh Renderer desmarcado**, no al revés:
el contenido de la escena se localiza recorriendo los objetos raíz con `FindObjectsOfType`, que
**no devuelve objetos desactivados**, así que un portador desactivado en la raíz no se encontraría
nunca. Con el renderer desmarcado no dibuja nada y sí se encuentra. De esto se encarga
[SetupDynamicShadowsScene.cs](DynamicShadows/Unity/DynamicShadows/Assets/Editor/SetupDynamicShadowsScene.cs).
Si algún shader no sobrevive al empaquetado se avisa en el log en vez de dibujar rosa en silencio.

Orden de trabajo, cada hito comprobable por sí solo:

| Hito | Config                                                     | Qué tiene que verse                                                          |
| ---- | ---------------------------------------------------------- | ---------------------------------------------------------------------------- |
| 1    | `PLAYER3D shadow` + bundle con suelo catcher y direccional | el juego intacto y **una sombra** del personaje en el suelo                  |
| 2    | añadir paredes y columnas al catcher                       | la sombra sube por la pared                                                  |
| 3    | `PLAYER3D only` + `CHARLIGHT 0`                            | **nada** cambia de aspecto en el personaje, y ya se ocluye tras las columnas |
| 4    | subir `CHARLIGHT` a 0.2–0.4                                | se oscurece al entrar en sombra                                              |
| 5    | luces puntuales en la escena                               | se tiñe al acercarse a una antorcha                                          |

El hito 3 es el que hay que mirar con lupa: es el único momento en que el personaje deja de
dibujarlo el juego. Si con `CHARLIGHT 0` se nota **cualquier** diferencia, la fórmula de color no
está bien reproducida y hay que arreglarla antes de seguir.

### 5.2c Lo que costó encontrar

Todos con el mismo patrón: **una comprobación que parecía verificar y no verificaba**. Van aquí
porque cada uno se puede volver a cometer.

**La matriz de `BGCAM_DEF` no es una rotación.** Sus filas llevan escala, y la de Y es `14/15`: el
framebuffer de 320×224 de PSX mostrado en 4:3, que FFIX guarda ahí para que los modelos casen con
el fondo pintado. Una cámara de Unity no puede llevarla — `Quaternion.LookRotation` ortonormaliza
en silencio lo que se le dé — así que hay que **sacarla de la vista y meterla en la proyección**:
`P00' = P00·kx/kz`, `P11' = P11·ky/kz`, `P23' = P23/kz`. El mismo error se cometió dos veces, una en
la cámara de Blender y otra en la del juego, con meses de distancia conceptual entre ellas.

> Y el diagnóstico decía `delta=(0.0,0.0)`. Comparaba la proyección derivada contra
> `PSX.CalculateGTE_RTPT`: **dos cálculos en C#, ninguno pasando por la cámara real**. Verificaba
> las matrices entre sí, no lo que Unity hace con ellas. Lo destapó dibujar la máscara en verde por
> encima del render del juego, que es la primera medición que sí atraviesa la cámara.

**Unity no escala el `range` de una luz con la transformada.** El contenedor multiplica por
`SCENESCALE` para pasar de metros a unidades de campo, pero el alcance de la luz no se entera: una
antorcha de 3 m acaba alcanzando 3 unidades, o sea 9 milímetros. Se convierte al adoptar la escena.

**Quitar el pase `ShadowCaster` a un shader le impide RECIBIR sombra.** La sombra direccional en
forward es en espacio de pantalla y se resuelve leyendo `_CameraDepthTexture`, que Unity construye
con el pase ShadowCaster de cada objeto. Sin él, el catcher no entra en la textura de profundidad y
su píxel consulta la sombra a la profundidad del fondo: iluminado siempre. Para que **no proyecte**,
el sitio es el desplegable _Cast Shadows_ del MeshRenderer, no quitar el pase.

**El descarte por stencil fue peor que el problema que arreglaba.** Recortaba sin mirar profundidad,
así que mordía la sombra del propio personaje donde su silueta la tocaba. La máscara de profundidad
basta y es correcta: descarta solo lo que está detrás. Si algo está de verdad delante, el juego está
pintando el fondo ahí, no al personaje, y oscurecerlo es lo que toca.

**Modular los píxeles del personaje mezclando sobre el render del juego no puede funcionar.** El
proxy multiplica lo que ENCUENTRE, y no sabe si el juego dibujó ahí al personaje o a un NPC que pasa
por delante: con un moguri delante aparecía el fantasma oscuro de Steiner encima. La luz va por
`_Color` del material del juego (ver `CHARLIGHT`).

**`LateUpdate` no es lo bastante tarde.** El orden entre MonoBehaviours es indefinido, así que un
actor cuya animación avanza el juego en su propio `LateUpdate` queda posado después del nuestro. Se
ve solo en lo que se mueve rápido — el pompón de un moguri, no un Steiner parado. El horneado va en
**`OnPreCull` de la cámara 3D**, el último instante antes de dibujar.

**Y `BakeMesh` aplica la escala DE MUNDO del renderer**, no la propia. `localScale = 1` en el proxy
es correcto siempre. "Corregirlo" con `lossy/local` espeja la malla por segunda vez y tumba a todos
los personajes — el `local (1,1,1)` frente al `lossy (-1,-1,1)` del log lo dice de un vistazo.

**Direct3D 9 no traga las variantes de sombra de luz puntual.** El juego corre en d3d9, donde Unity
compila a shader model 2.0 salvo que se pida `#pragma target 3.0` — y aun con él,
`multi_compile_fwdadd_fullshadows` genera los variantes de mapa de cubo (sombra de point light), que
no caben. El shader entero cae entonces al SubShader de respaldo, en silencio: `isSupported` sigue
siendo `true`. Se arregla con `#pragma skip_variants SHADOWS_CUBE POINT_COOKIE`, que conserva las
sombras de **foco** y descarta solo las de luz puntual.

> Y para saber qué SubShader está activo hay que mirar `Material.passCount`, que devuelve los del
> ACTIVO. Sin eso, "los focos no proyectan" es indistinguible de "el pase no compiló", y lo segundo
> solo se puede resolver adivinando. El cargador lo dice ahora al adoptar la escena.

**`scene.new(LINK_COPY)` no comparte: copia la lista de objetos de ese instante.** Un field con dos
BGCAM necesita dos escenas de Blender, porque la resolucion y el pixel aspect son de la ESCENA. Con
la copia enlazada, lo que se modelase despues aparecia **solo en la escena activa** -justo lo que se
suponia que resolvia- y la segunda escena heredaba ademas el `BackgroundPlate` de la primera, un
plano enorme con el fondo pintado en mitad de la sala. Lo que comparte de verdad es una
**coleccion**: `Escenario` enlazada en todas las escenas, y una coleccion por camara con su camara
y su fondo, enlazada solo en la suya.

> Se vio abriendo el `.blend` generado y listando que objetos tiene cada escena, mas un cubo de
> prueba para ver donde caia. **Nada de esto se nota mirando el archivo en Blender**: las dos
> escenas se ven bien recien generadas, y el fallo solo aparece al modelar.

**Restaurar un valor que era automatico lo vuelve manual.** `Camera.aspect` se deriva solo del
viewport **hasta que se le asigna**; a partir de ahi queda clavado. El exportador de fondos abre el
viewport para la captura y luego "restauraba" con `camera.aspect = previousAspect`, que no restaura
nada: fija el valor que hubiera en ese instante. Y el instante importa, porque el juego estrecha el
viewport del field un frame despues de entrar. La primera visita a un mapa exporta con el viewport
ya estrecho y clava el valor bueno **de casualidad**; al volver, exporta un frame antes, con la
pantalla entera, y clava 16:9 para siempre. El campo se dibuja entonces con una escala horizontal
que no es la de su viewport y el proxy deja de casar. Lo correcto es `camera.ResetAspect()` cuando
venia derivandose solo.

> El log lo tenia delante: `CAMERA ortho ... aspect=1.778 pixelRect=(x:77.68, width:1764.64 ...)`.
> 1764.64/1080 = **1.634**, no 1.778. **Un diagnostico que imprime dos cifras que tienen que cuadrar
> vale mas que uno que imprime la conclusion**, porque la conclusion sale bien aunque el sistema
> este mal -el `delta=(0.0,0.0)` de al lado seguia diciendo que todo cuadraba-.
>
> Y esa misma prisa se llevaba el export: en el mapa 150, al volver, el fondo salia de 1920x1080 con
> fovX 47.83 en vez de 1765x1080 con 44.36. El proyecto de Blender quedaba con una camara que no es
> la del juego, sin que nada avisara. Ahora se espera a que el viewport lleve tres frames quieto.

**`FindObjectsOfType` no ve lo desactivado, y eso convierte un diff en una trampa.** Lo que se
adopta de un bundle es la diferencia entre las raíces de antes de la carga aditiva y las de después.
Si la foto de ANTES se saca con `FindObjectsOfType`, un objeto del juego que estuviera apagado en ese
instante no sale en ella, y cuando el juego lo enciende unos frames más tarde parece "nuevo desde la
carga": se lo lleva el pase 3D, reparentado y cambiado de capa. **Falla solo al volver a un mapa**,
porque en la primera visita apenas hay objetos apagados. La foto de antes va con
`Resources.FindObjectsOfTypeAll`; la de después NO, porque esa devuelve también assets cargados.

> Sobrecoger en la foto de antes es gratis —como mucho deja algo sin adoptar, y eso se ve—.
> Sobrecoger en la de después es lo que rompe. **Las dos fotos de un diff no tienen por qué sacarse
> igual: cada una tiene su lado seguro por el que equivocarse.**

**Y parar de adoptar en el primer frame que da algo es apostar a que la carga aditiva entrega todas
sus raíces a la vez.** No lo garantiza. Lo que llegue después se queda fuera del contenedor: sin la
escala de `SCENESCALE` y en la capa que la cámara 3D no dibuja, o sea invisible y sin avisar.

**La limpieza no puede vivir solo en el gancho de salida.** Colgarla de `ff9ShutdownStateFieldMap`
la hace depender de que ese camino se recorra siempre —combate, menú, vídeo, volver al mismo mapa—.
Entrar en un mapa limpia ahora también, y avisa si encuentra algo vivo del anterior. Un proxy que
sobrevive a su mapa es una silueta de más en la máscara de profundidad.

**La caída de una luz de Unity no es "cuánta luz llega".** El catcher oscurece por
`alcance × (1 − sombra)`, y el alcance salía de `UnitySpotAttenuate`, que es **solo la caída por
distancia**. Esa caída es brutal — a media distancia del alcance ya va por el 13% —, así que un foco
de intensidad 3.5 que ilumina de sobra un plano `Standard` daba aquí un factor del 8%: una sombra
del 8% sobre un fondo prerenderizado no se ve. Lo que llega es **caída × intensidad**, y eso es
`_LightColor0.rgb`, que ya trae el color multiplicado por la intensidad.

> Costó tres intentos porque todo lo que se miró estaba bien: Unity generaba el mapa de sombras
> (probado poniendo un `PRIMITIVE_PLANE … LIT` con `Standard` al lado, que sí recibía la sombra),
> el shader compilaba entero (`passCount` = 4) y leía bien el mapa. **Un plano de material distinto
> junto al que falla es el discriminador más barato que hay**: separa "Unity no lo hace" de "mi
> shader no lo recoge" en una sola captura.
>
> Y lo que cerró el caso fue partir el factor en sus dos términos y pintar cada uno a solas en
> blanco y negro (`CATCHERDEBUG 2` y `3`). Mirar el resultado final solo dice "no se ve nada", que
> es compatible con cinco causas distintas. **Cuando un producto sale mal, hay que mirar los
> factores, no el producto.**

#### Las herramientas de diagnóstico que quedan

|                     | Qué mide                                                                                                                                                                                          |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `MASKDEBUG on`      | pinta el proxy en verde sobre el render del juego. Lo único que atraviesa la cámara real                                                                                                          |
| `CATCHERDEBUG 1..4` | saca a solas cada término del pase aditivo del catcher: 1 el pase entero en rojo, 2 la sombra, 3 el alcance, 4 el factor final. El modo va en el txt, así que se cambia sin reconstruir el bundle |
| `CAMERA`            | error de proyección por actor **y en el centro de la malla**, que es lo que detecta un error de escala: el origen puede estar clavado y el cuerpo no                                              |
| log al adoptar      | colliders, rangos de luz convertidos, **qué SubShader quedó activo**, shaders no soportados, batching estático                                                                                    |

La lección, en una línea: **una comprobación que no atraviesa el sistema real no es una comprobación.**

### 5.3 Modelar y llevar a Unity

Se modela sobre el fondo y el walkmesh reales. Después, en Unity 5.2.3:

1. Importar el FBX y colocarlo **sin mover** (las posiciones ya son correctas)
2. Luces, `Lightmap Static` en la geometría, `Baked GI` activo y `Precomputed Realtime GI` desactivado
3. `Window > Lighting > Build`
4. `Dynamic Shadows > Construir bundle` (menú de [BuildSceneBundle.cs](DynamicShadows/Unity/DynamicShadows/Assets/Editor/BuildSceneBundle.cs)),
   que escribe el `.unity3d` directamente en `DynamicShadows/Mod/DynamicShadows/`
5. Desplegar y **reiniciar el juego**

---

## 6. Referencia de `MemoriaFieldObjects.txt`

Vive en la raíz del juego, se relee **al cargar cada field**. Cambiar posiciones no requiere
recompilar: basta salir y entrar del mapa.

### Objetos

```
<fldMapNo> <modelo> <x> <y> <z> [escala] [LIT]
```

- `LIT` → lo dibuja la cámara 3D con shader `Standard`, con luz y sombras. Sin `LIT` usa la
  proyección PSX del juego.
- Modelos: un nombre GEO registrado, `PRIMITIVE_CUBE` o `PRIMITIVE_PLANE`.
- `@` delante de la X hace las coordenadas relativas a `bgi.charPos`. **Ojo**: eso _no_ es el punto
  de entrada (en el mapa 150 vale `(-1423, 0, 1347)` mientras se anda en Z 23..430).

### Ajustes globales

| Línea                                           | Efecto                                                                  |
| ----------------------------------------------- | ----------------------------------------------------------------------- |
| `SCENESCALE <factor>`                           | unidades de campo por unidad de escena (345)                            |
| `SCENEBUNDLE <mapa> <archivo> [escena]`         | carga un bundle de escena de Unity                                      |
| `AMBIENT <r> <g> <b> [intensidad]`              | luz ambiental del pase 3D, 0–1 por canal                                |
| `LIGHT <eulerX> <eulerY> <eulerZ> [intensidad]` | direccional creada por código; **no usar si el bundle ya trae la suya** |
| `SHADOWDISTANCE <unidades>`                     | por defecto son 40, insuficiente en escala de campo                     |
| `PLAYER3D off\|shadow\|full\|only`              | modo del proxy del personaje                                            |

### Diagnóstico

| Línea         | Efecto                                                                           |
| ------------- | -------------------------------------------------------------------------------- |
| `TRACE`       | posición del jugador en `Memoria.log` al andar, en unidades de campo y de escena |
| `CAMERA`      | compara la proyección PSX con la de la cámara derivada, e informa del proxy      |
| `DUMP`        | renderers y materiales de lo spawneado y del jugador                             |
| `PROBE`       | shaders y capacidades de sombra que sobrevivieron al stripping del build         |
| `EXPORTSCENE` | vuelca el mapa actual (§5.1)                                                     |

---

## 7. Trampas conocidas

Cada una de estas costó al menos una iteración. Están ordenadas por probabilidad de reaparecer.

**Material sin textura = invisible (vía PSX).** `PSX/FieldMapActor` descarta todo píxel donde
`alphaTextura * alphaColorVértice <= 0.5`. Sin textura, Unity usa la que declara el shader
(`"grey"`), cuyo alpha no es 1, y el modelo entero desaparece. El código asigna una textura blanca
de emergencia. _No aplica a los materiales que vienen dentro de un bundle._

**`Batching Static` rompe el escalado en runtime.** Unity precombina las mallas en tiempo de build
con su transformada horneada en los vértices, y el renderer ignora el transform después. Síntoma:
`SCENESCALE` no tiene efecto y la escena se ve a tamaño métrico. Solución:
`Edit > Project Settings > Player > Rendering` → desmarcar **Static Batching**. El cargador lo
detecta y avisa.

**La escala de campo rompe todo ajuste "por unidad" de Unity.** Ya nos mordió con `shadowDistance`
(40 por defecto) y con `Baked Resolution`. Aparecerá con tamaños de partícula, LOD y física. Regla:
si un ajuste viene en unidades, está pensado para metros.

**`AssetBundle.CreateFromFile` solo abre bundles sin comprimir.** `BuildStreamedSceneAssetBundle`
comprime por defecto (`UnityWeb`, LZMA). El script de editor pasa `BuildOptions.UncompressedAssetBundle`
y el cargador tiene un plan B con `CreateFromMemoryImmediate`. La cabecera del `.unity3d` lleva la
versión de Unity en texto plano — útil para diagnosticar.

**Regenerar el bundle exige reiniciar el juego.** Los bundles se quedan abiertos toda la sesión
porque `CreateFromFile` falla al abrir dos veces el mismo archivo.

**Cerrar Blender antes de regenerar el `.blend`.** Blender no bloquea el archivo; la instancia
abierta conserva la versión vieja en memoria y la escribe encima al guardar.

**El script de despliegue no sobrescribe `MemoriaFieldObjects.txt`** salvo con `-ResetConfig`. Es
deliberado (permite ajustar posiciones en el juego), pero explica varios "no funciona" que en
realidad eran configuración vieja.

**`bgi.charPos` no es el punto de entrada.** Es la posición por defecto que usa `FieldMap.AddPlayer`
solo en modo debug. Para coordenadas útiles, usar `TRACE`.

**Los bounds del personaje están inflados a propósito.** `FieldMapActor` los pone a
`Single.MaxValue * 0.01f` para desactivar el culling, porque la proyección PSX ocurre en el vertex
shader. Cualquier diagnóstico basado en `renderer.bounds` del jugador da basura.

**Comparar alturas a ojo no funciona.** En una vista cenital a 3/4 la profundidad se traduce en
altura de pantalla: un objeto más lejano se dibuja más arriba y parece más alto. Por eso el factor
de escala salió de la tabla del juego y no de la percepción.

**Una rotación de 180° no se ve.** A diferencia de un espejo, deja la escena con aspecto normal.
Solo se detecta midiendo el viaje completo hasta el juego.

---

## 8. Herramientas de verificación

| Herramienta                                                    | Uso                                                                                                                                                |
| -------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| [check_export.py](DynamicShadows/Tools/blender/check_export.py) | comprueba una exportación **sin abrir Blender**: proyecta el walkmesh por la ruta del juego y por la de Blender y reporta la desviación en píxeles |
| [dump_fbx.py](DynamicShadows/Tools/dump_fbx.py)                         | valida un FBX contra lo que exige el importador de Memoria (material obligatorio, UVs, `Lcl Scaling`)                                              |
| [make_cube_fbx.py](DynamicShadows/Tools/make_cube_fbx.py)               | genera un FBX de prueba con Blender headless                                                                                                       |
| `CAMERA` en el `.txt`                                          | verificación continua en el juego: `delta` debe ser `0.00`                                                                                         |

**Verificaciones superadas**, por si hay que rehacerlas tras un cambio grande:

```
coordenadas de campo → cámara del juego      delta 0.00 px
    (3 distancias de proyección, 2 cámaras, mapas estrechos y anchos, scroll en X e Y)
coordenadas de campo → proyecto de Blender   desviación máx. 0.00 px
Blender → FBX → Unity → juego                3 marcadores en ejes distintos, exactos
```

---

## 9. Estado y trabajo pendiente

### Hecho

- Cámara en perspectiva derivada del field, exacta al píxel
- Luz direccional real y sombras proyectadas
- Personaje en el pase 3D con sombra y profundidad correctas
- Carga de escenas de Unity con lightmaps horneados
- Exportador de mapas y generador de proyectos de Blender
- Escala calibrada y cadena de coordenadas verificada de punta a punta

### Hito 4 — sustituir el fondo

Cuando haya geometría real que enseñar. Sin incógnitas técnicas:

1. Dejar de dibujar `BGSCENE_DEF` y sus overlays
2. `clearFlags` de la cámara 3D de `Depth` a `SolidColor` o `Skybox`
3. Quitar la sombra falsa de `FieldMapActor.CreateShadowMesh`, que pasa a sobrar

### Cuestiones abiertas

**VFX del juego.** Las antorchas de FFIX son animaciones de fotogramas del fondo (`BGANIM_DEF`,
opcodes `EBG_anim*`) y **se pierden al sustituirlo**: hay que rehacerlas en la escena 3D. Los
efectos SPS (`SPSEffect`, humo, magia, lluvia) sí sobreviven porque son objetos aparte, pero se
dibujan en el pase PSX con profundidad falsa y compondrían mal contra geometría 3D: habría que
enrutarlos al pase 3D como se hizo con el personaje.

**Light probes.** `probes: 0` en todas las pruebas. Sin ellos, el personaje solo recibe la
direccional y la ambiental, y no reacciona a las luces locales del escenario. Alternativa sin
probes: separar en dos capas —escenario estático y personaje— y usar point lights en tiempo real
con `cullingMask` limitado a la capa del personaje.

**Partículas.** Los módulos del `ParticleSystem` (`emission`, `shape`, `colorOverLifetime`,
`textureSheetAnimation`) **no son accesibles por script en Unity 5.2** — llegaron en 5.3. Solo hay
propiedades de primer nivel. Un sistema de partículas decente es contenido de editor. Además no se
ha comprobado que los shaders `Particles/*` sobrevivieran al stripping: añadirlos a `PROBE` antes
de contar con ellos.

**Shaders propios.** Unity **no compila Cg/HLSL en runtime**: `ShadersLoader` usa
`new Material(shaderCode)` y los 140 subprogramas del repo son ensamblador `d3d9`. Un shader nuevo
habría que escribirlo así. Los built-in `Standard`, `Diffuse`, `Legacy Shaders/Diffuse`, `VertexLit`,
`Mobile/VertexLit` y `Unlit/Transparent Cutout` **sí** sobrevivieron al stripping y traen
ShadowCaster; `Bumped Diffuse`, `Mobile/Diffuse`, `Transparent/Cutout/Diffuse` y `Unlit/Texture` no.

---

## 10. Mapa de archivos

### Código del motor (`Assembly-CSharp/Memoria/Field/`)

| Archivo                     | Responsabilidad                                                        |
| --------------------------- | ---------------------------------------------------------------------- |
| `FieldPerspectiveCamera.cs` | derivación de la cámara, pase 3D, proxy del personaje, luz y ambiental |
| `CustomFieldObjects.cs`     | lectura de `MemoriaFieldObjects.txt`, spawn de objetos, diagnósticos   |
| `FieldSceneBundle.cs`       | carga de bundles de escena de Unity y adopción al pase 3D              |
| `FieldSceneExport.cs`       | exportación de cámara, fondo y walkmesh                                |

Puntos de enganche en el juego: `HonoluluFieldMain.ff9InitStateFieldMap` (spawn al cargar el mapa)
y `HonoluluFieldMain.HonoUpdate` (sincronización por frame).

### Herramientas

| Archivo                                       | Uso                                              |
| --------------------------------------------- | ------------------------------------------------ |
| `DynamicShadows/Tools/build-and-deploy.ps1`           | compilar y desplegar                                  |
| `DynamicShadows/Tools/blender/build_field_project.py` | generar el proyecto de Blender de un mapa             |
| `DynamicShadows/Tools/blender/check_export.py`        | verificar una exportación sin Blender                 |
| `DynamicShadows/Unity/.../Assets/Editor/`             | menús `Dynamic Shadows >` del editor de Unity         |
| `DynamicShadows/Tools/dump_fbx.py`, `make_cube_fbx.py`| utilidades de FBX                                     |

### Datos

- `DynamicShadows/Mod/DynamicShadows/` — el mod tal y como se instala: `ModDescription.xml`,
  `MemoriaFieldObjects.txt`, `DictionaryPatch.txt`, los bundles y los assets
- `MemoriaFieldObjects.txt` — configuración (la fuente; la copia viva está en la raíz del juego)
- `<juego>/MemoriaSceneExport/<mapa>/` — exportaciones
- `<juego>/Memoria.log` — log; se recrea en cada arranque

---

## 11. Nota de método

El patrón que ha funcionado, y que conviene mantener: **cuando algo no se ve, no adivinar**.
Añadir un diagnóstico que imprima el dato que distingue entre las hipótesis, y decidir con el
número. Varios de los errores de esta sesión —la inversión izquierda/derecha, el static batching, la
rotación de 180°— eran invisibles a simple vista y solo cayeron al medirlos.

Y al revés: dos de los diagnósticos iniciales fueron **falsos positivos** que costaron iteraciones
—los colores de vértice y el determinante de la matriz de vista—. Conviene comprobar una hipótesis
antes de construir sobre ella.
