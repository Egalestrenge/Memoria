# Lector minimo de FBX binario, para comprobar que un FBX cumple lo que el
# importador de Memoria necesita antes de meterlo en el juego.
#
# Uso:  python tools/dump_fbx.py <archivo.fbx>

import struct
import sys
import zlib

ARRAY_TYPES = {"f": "f", "d": "d", "l": "q", "i": "i", "b": "?"}
SCALAR_TYPES = {"Y": ("h", 2), "C": ("?", 1), "I": ("i", 4), "F": ("f", 4), "D": ("d", 8), "L": ("q", 8)}


class Node:
    def __init__(self, name):
        self.name = name
        self.props = []
        self.children = []

    def find(self, name):
        return [c for c in self.children if c.name == name]


def read_prop(f):
    kind = f.read(1).decode("ascii")
    if kind in SCALAR_TYPES:
        fmt, size = SCALAR_TYPES[kind]
        return struct.unpack("<" + fmt, f.read(size))[0]
    if kind in ARRAY_TYPES:
        length, encoding, comp_len = struct.unpack("<III", f.read(12))
        data = f.read(comp_len)
        if encoding == 1:
            data = zlib.decompress(data)
        return list(struct.unpack("<%d%s" % (length, ARRAY_TYPES[kind]), data))
    if kind in ("S", "R"):
        (length,) = struct.unpack("<I", f.read(4))
        raw = f.read(length)
        return raw.decode("utf-8", "replace") if kind == "S" else raw
    raise ValueError("Tipo de propiedad desconocido: %r" % kind)


def read_node(f, version):
    wide = version >= 7500
    fmt, size = ("<QQQB", 25) if wide else ("<IIIB", 13)
    head = f.read(size)
    if len(head) < size:
        return None
    end_offset, num_props, _prop_len, name_len = struct.unpack(fmt, head)
    if end_offset == 0:
        return None
    name = f.read(name_len).decode("utf-8", "replace")
    node = Node(name)
    for _ in range(num_props):
        node.props.append(read_prop(f))
    while f.tell() < end_offset:
        child = read_node(f, version)
        if child is None:
            break
        node.children.append(child)
    f.seek(end_offset)
    return node


def parse(path):
    with open(path, "rb") as f:
        # Cabecera: 20 bytes de texto + \x00\x1a\x00, y despues la version en 4 bytes
        magic = f.read(23)
        if not magic.startswith(b"Kaydara FBX Binary"):
            raise SystemExit("No es un FBX binario (Memoria tambien lee ASCII, pero este script no).")
        (version,) = struct.unpack("<I", f.read(4))
        root = Node("__root__")
        while True:
            node = read_node(f, version)
            if node is None:
                break
            root.children.append(node)
        return version, root


def main():
    version, root = parse(sys.argv[1])
    print("Version FBX: %d" % version)

    objects = root.find("Objects")
    connections = root.find("Connections")
    if not objects:
        raise SystemExit("FALLO: no hay seccion Objects")
    if not connections:
        raise SystemExit("FALLO: no hay seccion Connections (Memoria la necesita para materiales y esqueleto)")
    objects = objects[0]

    geometries = objects.find("Geometry")
    models = objects.find("Model")
    materials = objects.find("Material")
    print("Geometry: %d | Model: %d | Material: %d" % (len(geometries), len(models), len(materials)))

    if not materials:
        print("FALLO: sin Material -> GetMaterialIndex devuelve -1 -> IndexOutOfRangeException")
    for mat in materials:
        shading = [c for c in mat.children if c.name == "ShadingModel"]
        print("  Material %r  ShadingModel=%s" % (mat.props[1], shading[0].props[0] if shading else "?"))

    # Memoria hornea la transformada del nodo Model en los vertices
    # (FbxBone lee "Lcl Translation" / "Lcl Rotation" / "Lcl Scaling").
    for model in models:
        props = model.find("Properties70")
        wanted = {"Lcl Translation": None, "Lcl Rotation": None, "Lcl Scaling": None}
        if props:
            for p in props[0].find("P"):
                if p.props and p.props[0] in wanted:
                    wanted[p.props[0]] = tuple(p.props[4:7])
        print("  Model %r" % model.props[1])
        for key in ("Lcl Translation", "Lcl Rotation", "Lcl Scaling"):
            print("    %-16s %s" % (key, wanted[key] if wanted[key] else "(por defecto)"))

    for geo in geometries:
        verts = geo.find("Vertices")
        polys = geo.find("PolygonVertexIndex")
        uvs = geo.find("LayerElementUV")
        if not verts:
            continue
        v = verts[0].props[0]
        xs, ys, zs = v[0::3], v[1::3], v[2::3]
        print("  Geometry %r" % geo.props[1])
        print("    vertices: %d   indices de poligono: %d" % (len(v) // 3, len(polys[0].props[0]) if polys else 0))
        print("    X [%.2f, %.2f]  Y [%.2f, %.2f]  Z [%.2f, %.2f]"
              % (min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
        if uvs:
            mapping = [c for c in uvs[0].children if c.name == "MappingInformationType"]
            print("    UV: si (mapping=%s)" % (mapping[0].props[0] if mapping else "?"))
        else:
            print("    UV: NO -> el modelo se creara sin canal de textura")


if __name__ == "__main__":
    main()
