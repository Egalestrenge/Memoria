# Comprueba, sin abrir Blender, si el walkmesh exportado cae dentro del encuadre
# del fondo usando la camara exportada. Sirve para separar "los datos estan mal"
# de "el montaje en Blender esta mal".
#
#   python DynamicShadows/Tools/blender/check_export.py <carpeta_export>

import json
import math
import os
import struct
import sys


def read_png_size(path):
    with open(path, "rb") as handle:
        head = handle.read(24)
    if head[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    width, height = struct.unpack(">II", head[16:24])
    return width, height


def invert_rigid(right, up, forward, position):
    """Inversa de cameraToWorld, cuyas columnas son right/up/forward.

    OJO: no es la traspuesta. La base es ortogonal pero NO ortonormal (|up| vale
    1.0713), y para columnas ortogonales de norma k la inversa es la traspuesta
    dividida por k^2. Usar la traspuesta a secas mete un error del 7% en Y que
    ademas se compensa solo si el otro lado de la comparacion comete el mismo,
    con lo que todo parece cuadrar estando mal.
    """
    axes = (right, up, forward)  # columnas de cameraToWorld: M[r][c] = axes[c][r]
    inv_rot = [[axes[r][c] / sum(x * x for x in axes[r]) for c in range(3)] for r in range(3)]
    inv_pos = [-sum(inv_rot[r][c] * position[c] for c in range(3)) for r in range(3)]
    return inv_rot, inv_pos


def main():
    folder = os.path.abspath(sys.argv[1])
    with open(os.path.join(folder, "field.json"), encoding="utf-8") as handle:
        data = json.load(handle)

    width = data["renderWidth"]
    height = data["renderHeight"]
    tan_x = math.tan(data["fovXRadians"] / 2.0)
    tan_y = math.tan(data["fovYRadians"] / 2.0)

    inv_rot, inv_pos = invert_rigid(data["right"], data["up"], data["forward"], data["position"])

    vertices = []
    with open(os.path.join(folder, "walkmesh.obj"), encoding="utf-8") as handle:
        for line in handle:
            parts = line.split()
            if parts and parts[0] == "v":
                vertices.append((float(parts[1]), float(parts[2]), float(parts[3])))

    xs, ys, behind = [], [], 0
    for v in vertices:
        cam = [sum(inv_rot[r][c] * v[c] for c in range(3)) + inv_pos[r] for r in range(3)]
        # worldToCamera de Unity lleva un volteo de Z respecto al transform
        vx, vy, vz = cam[0], cam[1], -cam[2]
        if vz >= 0.0:
            behind += 1
            continue
        ndc_x = (vx / -vz) / tan_x + data["ndcOffsetX"]
        ndc_y = (vy / -vz) / tan_y + data["ndcOffsetY"]
        xs.append((ndc_x * 0.5 + 0.5) * width)
        ys.append((ndc_y * 0.5 + 0.5) * height)

    # Segunda ruta: reproducir lo que monta el script de Blender, para separar
    # "los datos estan mal" de "el montaje en Blender esta mal".
    scale = data["sceneScale"]

    def S(v):
        # Misma conversion que build_field_project.unity_to_blender
        return (-v[0], -v[2], v[1])

    def norm(v):
        return math.sqrt(sum(c * c for c in v))

    # La base exportada es ortogonal pero no ortonormal: |up| = 1.0713, el
    # estiramiento 320x224 -> 4:3 de PSX que FFIX lleva en la matriz de camara.
    # Una camara de Blender no puede llevar esa escala, asi que pasa a las
    # tangentes. El factor es k/kz porque el juego proyecta con la INVERSA de la
    # base, no con su traspuesta, y con escala no coinciden.
    kx, ky, kz = norm(data["right"]), norm(data["up"]), norm(data["forward"])
    btan_x = tan_x * kx / kz
    btan_y = tan_y * ky / kz
    angular = btan_x / btan_y

    xb = S([c / kx for c in data["right"]])
    yb = S([c / ky for c in data["up"]])
    zb = tuple(-c for c in S([c / kz for c in data["forward"]]))
    pb = tuple(c / scale for c in S(data["position"]))
    rb = (xb, yb, zb)  # columnas de la matriz de la camara de Blender

    shift_x = -data["ndcOffsetX"] / 2.0
    shift_y = -data["ndcOffsetY"] / 2.0 / angular

    bxs, bys = [], []
    for v in vertices:
        pw = tuple(c / scale for c in S(v))
        dv = tuple(pw[i] - pb[i] for i in range(3))
        cam = [sum(rb[c][i] * dv[i] for i in range(3)) for c in range(3)]
        if cam[2] >= 0.0:
            continue
        ndc_x = (cam[0] / -cam[2]) / btan_x - 2.0 * shift_x
        ndc_y = (cam[1] / -cam[2]) / btan_y - 2.0 * shift_y * angular
        bxs.append((ndc_x * 0.5 + 0.5) * width)
        bys.append((ndc_y * 0.5 + 0.5) * height)

    png = read_png_size(os.path.join(folder, "background.png"))

    print("mapa %s (%s)" % (data["map"], data["mapName"]))
    print("  render declarado : %sx%s" % (width, height))
    print("  background.png   : %sx%s" % png if png else "  background.png   : ilegible")
    print("  vertices         : %s  (detras de la camara: %s)" % (len(vertices), behind))
    if xs:
        print("  walkmesh en px   : X [%.0f, %.0f]   Y [%.0f, %.0f]" % (min(xs), max(xs), min(ys), max(ys)))
        inside = sum(1 for x, y in zip(xs, ys) if 0 <= x <= width and 0 <= y <= height)
        print("  dentro del cuadro: %s de %s" % (inside, len(xs)))
    if bxs:
        print("  via Blender  px  : X [%.0f, %.0f]   Y [%.0f, %.0f]" % (min(bxs), max(bxs), min(bys), max(bys)))
        err = max(max(abs(a - b) for a, b in zip(xs, bxs)), max(abs(a - b) for a, b in zip(ys, bys)))
        print("  desviacion max   : %.2f px" % err)


if __name__ == "__main__":
    main()
