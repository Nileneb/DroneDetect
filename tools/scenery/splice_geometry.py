"""Keep each original GLB (JSON, materials, textures) and only swap in the decimated geometry, so Unity's
glTFast importer produces the same sub-asset IDs. Usage: splice.py ORIG_DIR DECIMATED_DIR OUT_DIR"""
import json, os, struct, sys

def read(path):
    data = open(path, "rb").read()
    js_len = struct.unpack_from("<I", data, 12)[0]
    doc = json.loads(data[20:20 + js_len])
    off = 20 + js_len
    bin_len = struct.unpack_from("<I", data, off)[0]
    return doc, data[off + 8:off + 8 + bin_len]

def view_bytes(doc, binary, view_index):
    view = doc["bufferViews"][view_index]
    start = view.get("byteOffset", 0)
    return binary[start:start + view["byteLength"]]

def accessor_bytes(doc, binary, accessor):
    sizes = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
    comps = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}
    view = doc["bufferViews"][accessor["bufferView"]]
    if view.get("byteStride"):
        raise SystemExit("interleaved buffers are not supported")
    length = accessor["count"] * comps[accessor["type"]] * sizes[accessor["componentType"]]
    start = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    return binary[start:start + length]

orig_dir, dec_dir, out_dir = sys.argv[1:4]
for name in sorted(os.listdir(orig_dir)):
    if not name.endswith(".glb"):
        continue
    orig, orig_bin = read(os.path.join(orig_dir, name))
    dec, dec_bin = read(os.path.join(dec_dir, name))
    out = bytearray()
    views = []

    def append(data, target=None):
        out.extend(b"\0" * (-len(out) % 4))
        view = {"buffer": 0, "byteOffset": len(out), "byteLength": len(data)}
        if target:
            view["target"] = target
        out.extend(data)
        views.append(view)
        return len(views) - 1

    o_prim = orig["meshes"][0]["primitives"][0]
    d_prim = dec["meshes"][0]["primitives"][0]
    roles = [(o_prim["attributes"][k], d_prim["attributes"][k], 34962) for k in o_prim["attributes"]]
    roles.append((o_prim["indices"], d_prim["indices"], 34963))
    for o_index, d_index, target in roles:
        d_acc = dec["accessors"][d_index]
        o_acc = orig["accessors"][o_index]
        o_acc["bufferView"] = append(accessor_bytes(dec, dec_bin, d_acc), target)
        o_acc["byteOffset"] = 0
        for key in ("count", "componentType", "type", "min", "max", "normalized"):
            if key in d_acc:
                o_acc[key] = d_acc[key]
            else:
                o_acc.pop(key, None)
    for image in orig.get("images", []):
        image["bufferView"] = append(view_bytes(orig, orig_bin, image["bufferView"]))

    orig["bufferViews"] = views
    out.extend(b"\0" * (-len(out) % 4))
    orig["buffers"] = [{"byteLength": len(out)}]
    js = json.dumps(orig, separators=(",", ":")).encode()
    js += b" " * (-len(js) % 4)
    with open(os.path.join(out_dir, name), "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(out)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(out), 0x004E4942) + bytes(out))
    print(f"{name[:45]:45s} {os.path.getsize(os.path.join(orig_dir, name)) // 1_000_000:4d} MB -> {os.path.getsize(os.path.join(out_dir, name)) // 1_000_000:3d} MB, "
          f"{orig['accessors'][o_prim['indices']]['count'] // 3:,} triangles")
