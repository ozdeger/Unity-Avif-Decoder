# A small HEIF writer for the test images that avifenc cannot make: it reads the coded items of avifenc files and writes
# files of coded and derived items (iovl, iden, grid) with the properties and references given.
import struct


def box(kind, payload):
    return struct.pack(">I", 8 + len(payload)) + kind.encode() + payload


def full_box(kind, version, flags, payload):
    return box(kind, bytes([version]) + flags.to_bytes(3, "big") + payload)


def children(data, start, end):
    pos = start
    while pos + 8 <= end:
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        size = size or end - pos
        yield kind.decode("latin1"), pos + 8, pos + size
        pos += size


class Item:
    def __init__(self, kind, data=b"", properties=None, essential=None, references=None, name=""):
        self.kind = kind                    # 'av01', 'iovl', 'iden', 'grid', 'Exif', ...
        self.data = data
        self.properties = properties or []  # property box bytes, in association order
        self.essential = essential or []    # indices into properties that are essential
        self.references = references or {}  # {'dimg': [items], 'auxl': [items], 'prem': [items]}
        self.name = name
        self.source = None                  # an Item whose data this item is (iloc construction method 2)
        self.id = 0


def read_avifenc(path):
    """The coded items of an avifenc file: [(item, payload, properties)], the colour item first, then its alpha."""
    data = open(path, "rb").read()
    items, props, assoc, locs = {}, [], {}, {}
    for kind, start, end in children(data, 0, len(data)):
        if kind != "meta":
            continue
        for k, s, e in children(data, start + 4, end):
            if k == "iinf":
                version = data[s]
                p = s + 4 + (2 if version == 0 else 4)
                for ik, is_, ie in children(data, p, e):
                    item_id, = struct.unpack(">H", data[is_ + 4:is_ + 6])
                    items[item_id] = data[is_ + 8:is_ + 12].decode()
            elif k == "iprp":
                for pk, ps, pe in children(data, s, e):
                    if pk == "ipco":
                        props = [data[cs - 8:ce] for _, cs, ce in children(data, ps, pe)]
                    elif pk == "ipma":
                        count, = struct.unpack(">I", data[ps + 4:ps + 8])
                        p = ps + 8
                        for _ in range(count):
                            item_id, n = struct.unpack(">HB", data[p:p + 3])
                            p += 3
                            assoc[item_id] = [data[p + i] & 0x7F for i in range(n)]
                            p += n
            elif k == "iloc":
                version = data[s]
                sizes = data[s + 4]
                assert sizes == 0x44 and data[s + 5] == 0
                count, = struct.unpack(">H", data[s + 6:s + 8])
                p = s + 8
                for _ in range(count):
                    item_id, = struct.unpack(">H", data[p:p + 2])
                    p += 2
                    method = 0
                    if version == 1:
                        method = struct.unpack(">H", data[p:p + 2])[0] & 15
                        p += 2
                    p += 2
                    extents, = struct.unpack(">H", data[p:p + 2])
                    p += 2
                    chunks = []
                    for _ in range(extents):
                        offset, length = struct.unpack(">II", data[p:p + 8])
                        p += 8
                        chunks.append(data[offset:offset + length])
                    assert method == 0
                    locs[item_id] = b"".join(chunks)
    result = []
    for item_id in sorted(items):
        if items[item_id] != "av01":
            continue
        properties = [props[i - 1] for i in assoc.get(item_id, [])]
        result.append(Item("av01", locs[item_id], properties, essential=[]))
    return result


def ispe(width, height):
    return full_box("ispe", 0, 0, struct.pack(">II", width, height))


def irot(angle):
    return box("irot", bytes([angle & 3]))


def imir(axis):
    return box("imir", bytes([axis & 1]))


def lsel(layer):
    return box("lsel", struct.pack(">H", layer))


def auxc(urn):
    return full_box("auxC", 0, 0, urn.encode() + b"\0")


def nclx(primaries, transfer, matrix, full_range):
    return box("colr", b"nclx" + struct.pack(">HHHB", primaries, transfer, matrix, 0x80 if full_range else 0))


def pixi(depths):
    return full_box("pixi", 0, 0, bytes([len(depths)]) + bytes(depths))


def av1c_from_obus(obus, depth, mono=False, sub_x=1, sub_y=1):
    """An av1C box for a stream of OBUs: the configuration fields and the sequence header as configOBUs."""
    seq = None
    pos = 0
    while pos < len(obus):
        header = obus[pos]
        kind = (header >> 3) & 15
        ext = (header >> 2) & 1
        p = pos + 1 + ext
        size = 0
        for i in range(8):
            b = obus[p + i]
            size |= (b & 0x7F) << (7 * i)
            if not b & 0x80:
                p += i + 1
                break
        if kind == 1:
            seq = obus[pos:p + size]
            profile = obus[p] >> 5
        pos = p + size
    flags = (0x40 if depth > 8 else 0) | (0x20 if depth == 12 else 0) | (0x10 if mono else 0) | (sub_x << 3) | (sub_y << 2)
    return box("av1C", bytes([0x81, (profile << 5) | 8, flags, 0]) + seq)


def drop_temporal_delimiters(obus):
    out = bytearray()
    pos = 0
    while pos < len(obus):
        header = obus[pos]
        kind = (header >> 3) & 15
        ext = (header >> 2) & 1
        p = pos + 1 + ext
        size = 0
        for i in range(8):
            b = obus[p + i]
            size |= (b & 0x7F) << (7 * i)
            if not b & 0x80:
                p += i + 1
                break
        if kind != 2:
            out += obus[pos:p + size]
        pos = p + size
    return bytes(out)


def write(path, items, primary, groups=()):
    """Writes the items (primary is one of them) with their properties, references and entity groups into one file."""
    for index, item in enumerate(items):
        item.id = index + 1
    properties = []
    associations = []
    for item in items:
        indices = []
        for i, prop in enumerate(item.properties):
            if prop not in properties:
                properties.append(prop)
            index = properties.index(prop) + 1
            indices.append(index | (0x80 if i in item.essential else 0))
        associations.append((item.id, indices))

    ftyp = box("ftyp", b"avif" + struct.pack(">I", 0) + b"avifmif1miaf")
    hdlr = full_box("hdlr", 0, 0, b"\0" * 4 + b"pict" + b"\0" * 12 + b"\0")
    pitm = full_box("pitm", 0, 0, struct.pack(">H", primary.id))
    infes = b""
    for item in items:
        infes += full_box("infe", 2, 0, struct.pack(">HH", item.id, 0) + item.kind.encode() + item.name.encode() + b"\0")
    iinf = full_box("iinf", 0, 0, struct.pack(">H", len(items)) + infes)
    refs = b""
    for item in items:
        if item.source is not None:
            item.references = dict(item.references, iloc=[item.source])
    for item in items:
        for kind, targets in item.references.items():
            refs += box(kind, struct.pack(">HH", item.id, len(targets)) + b"".join(struct.pack(">H", t.id) for t in targets))
    iref = full_box("iref", 0, 0, refs) if refs else b""
    ipco = box("ipco", b"".join(properties))
    entries = b""
    for item_id, indices in associations:
        entries += struct.pack(">HB", item_id, len(indices)) + bytes(indices)
    ipma = full_box("ipma", 0, 0, struct.pack(">I", len(associations)) + entries)
    iprp = box("iprp", ipco + ipma)
    grpl = b""
    for group_id, (kind, members) in enumerate(groups, start=1000):
        grpl += full_box(kind, 0, 0, struct.pack(">II", group_id, len(members)) + b"".join(struct.pack(">I", m.id) for m in members))
    grpl = box("grpl", grpl) if grpl else b""

    # Version 1 of iloc, which has the construction method, only when an item is built from another
    version = 1 if any(item.source is not None for item in items) else 0

    def build(base):
        locations = b""
        offset = base
        for item in items:
            method = struct.pack(">H", 2 if item.source is not None else 0) if version else b""
            if item.source is not None:
                locations += struct.pack(">H", item.id) + method + struct.pack(">HHII", 0, 1, 0, len(item.source.data))
            elif item.data:
                locations += struct.pack(">H", item.id) + method + struct.pack(">HHII", 0, 1, offset, len(item.data))
            else:
                locations += struct.pack(">H", item.id) + method + struct.pack(">HH", 0, 0)
            offset += len(item.data)
        iloc = full_box("iloc", version, 0, bytes([0x44, 0x00]) + struct.pack(">H", len(items)) + locations)
        return box("meta", b"\0\0\0\0" + hdlr + pitm + iloc + iinf + iref + iprp + grpl)

    meta = build(0)
    meta = build(len(ftyp) + len(meta) + 8)
    open(path, "wb").write(ftyp + meta + box("mdat", b"".join(item.data for item in items)))
