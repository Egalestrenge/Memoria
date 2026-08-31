# Checks, without opening Blender, whether the exported walkmesh falls inside the
# background frame using the exported camera. It is there to separate "the data is
# wrong" from "the Blender setup is wrong".
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
    """Inverse of cameraToWorld, whose columns are right/up/forward.

    CAREFUL: this is not the transpose. The basis is orthogonal but NOT orthonormal
    (|up| is 1.0713), and for orthogonal columns of norm k the inverse is the
    transpose divided by k^2. Using the bare transpose introduces a 7% error in Y
    that cancels itself out if the other side of the comparison makes the same
    mistake, so everything appears to line up while being wrong.
    """
    axes = (right, up, forward)  # columns of cameraToWorld: M[r][c] = axes[c][r]
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
        # Unity's worldToCamera carries a Z flip with respect to the transform
        vx, vy, vz = cam[0], cam[1], -cam[2]
        if vz >= 0.0:
            behind += 1
            continue
        ndc_x = (vx / -vz) / tan_x + data["ndcOffsetX"]
        ndc_y = (vy / -vz) / tan_y + data["ndcOffsetY"]
        xs.append((ndc_x * 0.5 + 0.5) * width)
        ys.append((ndc_y * 0.5 + 0.5) * height)

    # Second route: reproduce what the Blender script builds, to separate
    # "the data is wrong" from "the Blender setup is wrong".
    scale = data["sceneScale"]

    def S(v):
        # Same conversion as build_field_project.unity_to_blender
        return (-v[0], -v[2], v[1])

    def norm(v):
        return math.sqrt(sum(c * c for c in v))

    # The exported basis is orthogonal but not orthonormal: |up| = 1.0713, the PSX
    # 320x224 -> 4:3 stretch that FFIX carries in its camera matrix. A Blender camera
    # cannot carry that scale, so it moves into the tangents. The factor is k/kz
    # because the game projects with the INVERSE of the basis, not with its transpose,
    # and with scale present the two are not the same.
    kx, ky, kz = norm(data["right"]), norm(data["up"]), norm(data["forward"])
    btan_x = tan_x * kx / kz
    btan_y = tan_y * ky / kz
    angular = btan_x / btan_y

    xb = S([c / kx for c in data["right"]])
    yb = S([c / ky for c in data["up"]])
    zb = tuple(-c for c in S([c / kz for c in data["forward"]]))
    pb = tuple(c / scale for c in S(data["position"]))
    rb = (xb, yb, zb)  # columns of the Blender camera matrix

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
    print("  vertices         : %s  (behind the camera: %s)" % (len(vertices), behind))
    if xs:
        print("  walkmesh in px   : X [%.0f, %.0f]   Y [%.0f, %.0f]" % (min(xs), max(xs), min(ys), max(ys)))
        inside = sum(1 for x, y in zip(xs, ys) if 0 <= x <= width and 0 <= y <= height)
        print("  inside the frame : %s of %s" % (inside, len(xs)))
    if bxs:
        print("  via Blender  px  : X [%.0f, %.0f]   Y [%.0f, %.0f]" % (min(bxs), max(bxs), min(bys), max(bys)))
        err = max(max(abs(a - b) for a, b in zip(xs, bxs)), max(abs(a - b) for a, b in zip(ys, bys)))
        print("  desviacion max   : %.2f px" % err)


if __name__ == "__main__":
    main()
