# The test images beyond single coded images: alpha, YCgCo, 16-bit references, image sequences, metadata, derived images
# (overlay, identity, grid of derived tiles) and the matrix coefficients that libavif does not decode. make.py calls build().
import math, os, shutil, struct, subprocess, sys, zlib
import numpy as np
from PIL import Image

import heif

SIXTEEN_BIT = ["d10_444", "d12_420", "alpha10_444", "chroma_derived_bt2020", "bt2020", "grain16_d10"]


def run(*args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"failed: {' '.join(args)}\n{result.stdout}{result.stderr}")


def write_png16(path, rgb):
    """A 16-bit RGB or RGBA PNG (Pillow writes 16-bit grey only)."""
    h, w, c = rgb.shape
    raw = b"".join(b"\0" + rgb[y].astype(">u2").tobytes() for y in range(h))
    chunk = lambda kind, data: struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    header = struct.pack(">IIBBBBB", w, h, 16, 2 if c == 3 else 6, 0, 0, 0)
    open(path, "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def rgba_picture(photo, w, h, seed):
    """A photo with an alpha of a soft disc, a hard-edged hole and a gradient, so every alpha level appears."""
    rgb = np.asarray(photo(w, h, seed).convert("RGB"))
    y, x = np.mgrid[0:h, 0:w]
    disc = np.clip(1.3 - np.hypot((x - w * 0.55) / (w * 0.45), (y - h * 0.5) / (h * 0.45)), 0, 1)
    alpha = (disc * 255 * (x / max(w - 1, 1) * 0.6 + 0.4)).astype(np.uint8)
    alpha[h // 5:h // 3, w // 6:w // 3] = 0
    return Image.fromarray(np.dstack([rgb, alpha]), "RGBA")


def build(here, tmp, photo):
    src = lambda name: os.path.join(tmp, name + ".png")
    rgba_picture(photo, 256, 256, 11).save(src("rgba256"))
    rgba_picture(photo, 512, 384, 12).save(src("rgba512x384"))
    cases = {
        # Alpha: straight, premultiplied, 10-bit, in a grid of tiles that each have an alpha item
        "alpha420": ["-y", "420", "-q", "60", src("rgba256")],
        "alpha_premultiplied": ["-y", "420", "-q", "60", "-p", src("rgba256")],
        "alpha10_444": ["-d", "10", "-y", "444", "-q", "60", src("rgba256")],
        "alpha_grid": ["-g", "2x2", "-y", "420", "-q", "60", src("rgba512x384")],
        # YCgCo (matrix 8) in 4:4:4 and 4:2:0, and lossless YCgCo-Re (16), 2 bits deeper than its RGB
        "ycgco444": ["--cicp", "1/13/8", "-r", "full", "-y", "444", "-q", "60", os.path.join(tmp, "photo256.png")],
        "ycgco420": ["--cicp", "1/13/8", "-r", "full", "-y", "420", "-q", "60", os.path.join(tmp, "photo256.png")],
        "ycgco_re": ["-l", "--cicp", "1/13/16", "-d", "10", "-y", "444", os.path.join(tmp, "photo256.png")],
    }
    for name, args in cases.items():
        run("avifenc", "-j", "4", *args, os.path.join(here, name + ".avif"))

    build_sequences(here, tmp, photo)
    build_metadata(here, tmp)
    build_derived(here, tmp, photo)
    build_nonlinear(here, tmp)
    build_gain_map(here, tmp, photo)
    build_fragments(here, tmp, photo)
    build_layered(here, tmp, photo)
    build_unsupported(here, tmp)
    build_container(here, tmp)
    build_limits(here, tmp)
    build_limited_alpha(here, tmp)


# A layered image of two inputs (avifenc --layered): the second layer is an inter frame predicted from the first
def build_layered(here, tmp, photo):
    photo(256, 192, 61).convert("RGB").save(os.path.join(tmp, "layer.png"))
    run("avifenc", "-j", "4", "--layered", "-q", "20", os.path.join(tmp, "layer.png"), "-q:u", "70", os.path.join(tmp, "layer.png"), os.path.join(here, "layered2.avif"))


def references_16bit(here):
    folder = os.path.join(here, "16bit")
    os.makedirs(folder, exist_ok=True)
    for name in SIXTEEN_BIT:
        run("avifdec", "-d", "16", "--png-compress", "9", os.path.join(here, name + ".avif"), os.path.join(folder, name + ".png"))


# Image sequences: 4 frames at 10 per second, repeated twice more; one opaque, one with an alpha track. Each frame is a key
# frame here; the inter-coded sequences are in sequences/inter.
def build_sequences(here, tmp, photo):
    folder = os.path.join(here, "sequences")
    os.makedirs(folder, exist_ok=True)
    frames = []
    for i in range(4):
        path = os.path.join(tmp, f"frame{i}.png")
        rgba_picture(photo, 96, 64, 20 + i).save(path)
        frames.append(path)
    opaque = []
    for i, frame in enumerate(frames):
        path = os.path.join(tmp, f"opaque{i}.png")
        Image.open(frame).convert("RGB").save(path)
        opaque.append(path)
    common = ["-j", "4", "--fps", "10", "--repetition-count", "2", "-q", "60", "-y", "420"]
    run("avifenc", *common, "-k", "1", *opaque, os.path.join(folder, "keyframes.avif"))
    run("avifenc", *common, "-k", "1", *frames, os.path.join(folder, "keyframes_alpha.avif"))
    # Inter-coded: only the first frame is a key frame
    run("avifenc", *common, *opaque, os.path.join(folder, "inter.avif"))
    run("avifenc", *common, "-d", "10", "-y", "444", *frames, os.path.join(folder, "inter_alpha_10bit.avif"))
    for name in ("keyframes", "keyframes_alpha", "inter", "inter_alpha_10bit"):
        for i in range(4):
            run("avifdec", "--index", str(i), "-d", "8", "--png-compress", "9", os.path.join(folder, name + ".avif"), os.path.join(folder, f"{name}.{i}.png"))


# Exif, XMP and ICC payloads, content light level, and the crop, rotation and mirroring, which Identify reports
def build_metadata(here, tmp):
    folder = os.path.join(here, "metadata")
    os.makedirs(folder, exist_ok=True)
    exif = b"MM\x00\x2a\x00\x00\x00\x08\x00\x00\x00\x00\x00\x00"
    xmp = b'<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"/></x:xmpmeta>'
    icc = struct.pack(">I", 132) + b"test" + bytes(120) + struct.pack(">I", 0)
    for name, data in (("exif.bin", exif), ("xmp.xml", xmp), ("profile.icc", icc)):
        open(os.path.join(folder, name), "wb").write(data)
    run("avifenc", "-j", "4", "-q", "60", "--exif", os.path.join(folder, "exif.bin"), "--xmp", os.path.join(folder, "xmp.xml"),
        "--icc", os.path.join(folder, "profile.icc"), "--clli", "1000,400", "--crop", "64,32,320,240", "--irot", "1", "--imir", "1",
        os.path.join(tmp, "photo512x384.png"), os.path.join(folder, "metadata.avif"))


def decode_rgba(path, tmp):
    out = os.path.join(tmp, "decoded.png")
    run("avifdec", "-d", "8", path, out)
    return np.asarray(Image.open(out).convert("RGBA")).astype(np.int64)


# Derived images that avifenc does not write: an overlay of an opaque and a transparent image on a translucent fill colour,
# an identity derivation that rotates and mirrors, and a grid whose tiles are identity derivations. The references are the
# libavif decodes of the inputs, composited as ISO/IEC 23008-12 6.6.2 says, with straight alpha.
def build_derived(here, tmp, photo):
    folder = os.path.join(here, "derived")
    os.makedirs(folder, exist_ok=True)
    photo(128, 96, 31).convert("RGB").save(os.path.join(tmp, "a.png"))
    photo(128, 96, 32).convert("RGB").save(os.path.join(tmp, "a2.png"))
    rgba_picture(photo, 100, 80, 33).save(os.path.join(tmp, "b.png"))
    for name, extra in (("a", ["-y", "444"]), ("a2", ["-y", "444"]), ("b", ["-y", "420"])):
        run("avifenc", "-j", "4", "-q", "70", *extra, os.path.join(tmp, name + ".png"), os.path.join(tmp, name + ".avif"))
    a = heif.read_avifenc(os.path.join(tmp, "a.avif"))[0]
    a2 = heif.read_avifenc(os.path.join(tmp, "a2.avif"))[0]
    b_color, b_alpha = heif.read_avifenc(os.path.join(tmp, "b.avif"))
    b_alpha.references = {"auxl": [b_color]}
    pa = decode_rgba(os.path.join(tmp, "a.avif"), tmp)
    pa2 = decode_rgba(os.path.join(tmp, "a2.avif"), tmp)
    pb = decode_rgba(os.path.join(tmp, "b.avif"), tmp)

    # Overlay: canvas 300 x 200, fill (0x8000, 0x4000, 0xC000, 0x8000), a at (-20, 30), b at (150, 60)
    fill16 = [0x8000, 0x4000, 0xC000, 0x8000]
    offsets = [(-20, 30), (150, 60)]
    data = bytes([0, 0]) + struct.pack(">HHHH", *fill16) + struct.pack(">HH", 300, 200)
    data += b"".join(struct.pack(">hh", x, y) for x, y in offsets)
    overlay = heif.Item("iovl", data, [heif.ispe(300, 200)], references={"dimg": [a, b_color]})
    heif.write(os.path.join(folder, "overlay.avif"), [overlay, a, b_color, b_alpha], overlay)
    canvas = np.zeros((200, 300, 4), np.int64)
    canvas[...] = [(v * 255 + 32767) // 65535 for v in fill16]
    for image, (ox, oy) in zip((pa, pb), offsets):
        for y in range(max(0, oy), min(200, oy + image.shape[0])):
            for x in range(max(0, ox), min(300, ox + image.shape[1])):
                s = image[y - oy, x - ox]
                d = canvas[y, x]
                sa, da = s[3], d[3]
                if sa == 0:
                    continue
                total = sa * 255 + da * (255 - sa)
                for c in range(3):
                    canvas[y, x, c] = (s[c] * sa * 255 + d[c] * da * (255 - sa) + total // 2) // total
                canvas[y, x, 3] = (total + 127) // 255
    Image.fromarray(canvas.astype(np.uint8), "RGBA").save(os.path.join(folder, "overlay.png"))

    # Identity: a rotated a quarter turn anticlockwise, then mirrored top to bottom
    identity = heif.Item("iden", b"", [heif.ispe(128, 96), heif.irot(1), heif.imir(0)], essential=[1, 2], references={"dimg": [a]})
    heif.write(os.path.join(folder, "identity.avif"), [identity, a], identity)
    expected = np.flipud(np.rot90(pa, 1))
    Image.fromarray(expected.astype(np.uint8), "RGBA").save(os.path.join(folder, "identity.png"))

    # A 2 x 1 grid of identity derivations that turn a and a2 half a turn, cropped to 250 x 90
    tiles = [heif.Item("iden", b"", [heif.ispe(128, 96), heif.irot(2)], essential=[1], references={"dimg": [image]}) for image in (a, a2)]
    grid = heif.Item("grid", bytes([0, 0, 0, 1]) + struct.pack(">HH", 250, 90), [heif.ispe(250, 90)], references={"dimg": tiles})
    heif.write(os.path.join(folder, "grid_of_identities.avif"), [grid, *tiles, a, a2], grid)
    expected = np.concatenate([np.rot90(pa, 2), np.rot90(pa2, 2)], axis=1)[:90, :250]
    Image.fromarray(expected.astype(np.uint8), "RGBA").save(os.path.join(folder, "grid_of_identities.png"))


# ITU-T H.273 transfer characteristics, encoded to linear and linear to encoded
BT709_A, BT709_B = 1.09929682680944, 0.018053968510807
PQ = (2610 / 16384, 2523 / 4096 * 128, 3424 / 4096, 2413 / 4096 * 32, 2392 / 4096 * 32)
HLG_A = 0.17883277
HLG_B = 1 - 4 * HLG_A
HLG_C = 0.5 - HLG_A * math.log(4 * HLG_A)


def to_linear(tc, v):
    v = min(max(v, 0.0), 1.0)
    if tc in (1, 6, 14, 15):
        return v / 4.5 if v < 4.5 * BT709_B else ((v + BT709_A - 1) / BT709_A) ** (1 / 0.45)
    if tc == 13:
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
    if tc == 16:
        m1, m2, c1, c2, c3 = PQ
        p = v ** (1 / m2)
        return (max(p - c1, 0) / (c2 - c3 * p)) ** (1 / m1)
    if tc == 17:
        return v ** 2.6 * (52.37 / 48)
    if tc == 18:
        return v * v / 3 if v <= 0.5 else (math.exp((v - HLG_C) / HLG_A) + HLG_B) / 12
    raise ValueError(tc)


def to_encoded(tc, l):
    l = max(l, 0.0)
    if tc in (1, 6, 14, 15):
        v = 4.5 * l if l < BT709_B else BT709_A * l ** 0.45 - (BT709_A - 1)
    elif tc == 13:
        v = 12.92 * l if l <= 0.0031308 else 1.055 * l ** (1 / 2.4) - 0.055
    elif tc == 16:
        m1, m2, c1, c2, c3 = PQ
        y = l ** m1
        v = ((c1 + c2 * y) / (1 + c3 * y)) ** m2
    elif tc == 17:
        v = (48 * l / 52.37) ** (1 / 2.6)
    elif tc == 18:
        v = math.sqrt(3 * l) if l <= 1 / 12 else HLG_A * math.log(12 * l - HLG_B) + HLG_C
    else:
        raise ValueError(tc)
    return min(v, 1.0)


def chromaticity_weights(primaries):
    # Equations 32 to 37 of H.273 on the chromaticities of libavif's table
    table = {9: (0.708, 0.292, 0.170, 0.797, 0.131, 0.046, 0.3127, 0.3290), 12: (0.680, 0.320, 0.265, 0.690, 0.150, 0.060, 0.3127, 0.3290)}
    rx, ry, gx, gy, bx, by, wx, wy = table[primaries]
    rz, gz, bz, wz = 1 - rx - ry, 1 - gx - gy, 1 - bx - by, 1 - wx - wy
    den = wy * (rx * (gy * bz - by * gz) + gx * (by * rz - ry * bz) + bx * (ry * gz - gy * rz))
    kr = ry * (wx * (gy * bz - by * gz) + wy * (bx * gz - gx * bz) + wz * (gx * by - bx * gy)) / den
    kb = by * (wx * (ry * gz - gy * rz) + wy * (gx * rz - rx * gz) + wz * (rx * gy - gx * ry)) / den
    return round(kr * 100000) / 100000, round(kb * 100000) / 100000


RGB_TO_LMS = np.array([[1688, 2146, 262], [683, 2951, 462], [99, 309, 3688]]) / 4096
LMS_TO_ICTCP = {16: np.array([[2048, 2048, 0], [6610, -13613, 7003], [17933, -17390, -543]]) / 4096,
                18: np.array([[2048, 2048, 0], [3625, -7465, 3840], [9500, -9212, -288]]) / 4096}


def forward(matrix, primaries, tc, rgb):
    """R'G'B' in [0, 1] to E'Y, E'PB and E'PR, by the definitions of H.273."""
    r, g, b = rgb
    if matrix == 11:
        return g, (0.986566 * b - g) / 2, (r - 0.991902 * g) / 2
    if matrix == 14:
        lin = RGB_TO_LMS @ np.array([to_linear(tc, r), to_linear(tc, g), to_linear(tc, b)])
        lms = np.array([to_encoded(tc, v) for v in lin])
        return tuple(LMS_TO_ICTCP[tc] @ lms)
    kr, kb = (0.2627, 0.0593) if matrix == 10 else chromaticity_weights(primaries)
    kg = 1 - kr - kb
    y = to_encoded(tc, kr * to_linear(tc, r) + kg * to_linear(tc, g) + kb * to_linear(tc, b))
    nb, pb = to_encoded(tc, 1 - kb), 1 - to_encoded(tc, kb)
    nr, pr = to_encoded(tc, 1 - kr), 1 - to_encoded(tc, kr)
    db, dr = b - y, r - y
    return y, db / (2 * (nb if db <= 0 else pb)), dr / (2 * (nr if dr <= 0 else pr))


def inverse(matrix, primaries, tc, ey, epb, epr):
    if matrix == 11:
        return 0.991902 * ey + 2 * epr, ey, (ey + 2 * epb) / 0.986566
    if matrix == 14:
        lms = np.linalg.inv(LMS_TO_ICTCP[tc]) @ np.array([ey, epb, epr])
        lin = np.linalg.inv(RGB_TO_LMS) @ np.array([to_linear(tc, v) for v in lms])
        return tuple(to_encoded(tc, v) for v in lin)
    kr, kb = (0.2627, 0.0593) if matrix == 10 else chromaticity_weights(primaries)
    kg = 1 - kr - kb
    nb, pb = to_encoded(tc, 1 - kb), 1 - to_encoded(tc, kb)
    nr, pr = to_encoded(tc, 1 - kr), 1 - to_encoded(tc, kr)
    eb = ey + 2 * (nb if epb < 0 else pb) * epb
    er = ey + 2 * (nr if epr < 0 else pr) * epr
    green = (to_linear(tc, ey) - kr * to_linear(tc, er) - kb * to_linear(tc, eb)) / kg
    return er, to_encoded(tc, green), eb


# The matrices that libavif refuses (10, 11, 13 and 14), and YCgCo-Ro, limited-range YCgCo and limited-range identity, which
# avifenc does not write: a picture is taken to Y, Cb and Cr by the definitions of H.273, coded losslessly in 10-bit 4:4:4
# with aomenc, and the reference is the exact inverse of the coded samples
def build_nonlinear(here, tmp):
    folder = os.path.join(here, "nonlinear")
    os.makedirs(folder, exist_ok=True)
    w, h = 48, 32
    yy, xx = np.mgrid[0:h, 0:w]
    rgb = np.stack([0.08 + 0.84 * xx / (w - 1), 0.08 + 0.84 * yy / (h - 1), 0.08 + 0.84 * ((xx + yy) % 17) / 16], -1)
    to16 = lambda values: [0 if not v > 0 else 65535 if v >= 1 else math.floor(v * 65535 + 0.5) for v in values]
    cases = {"bt2020_cl": (10, 9, 14, True), "chroma_derived_cl": (13, 12, 13, True), "smpte2085": (11, 10, 17, True), "ictcp_pq": (14, 9, 16, True),
             "ictcp_hlg": (14, 9, 18, True), "ycgco_ro": (17, 1, 13, True), "ycgco_limited": (8, 1, 13, False), "identity_limited": (0, 1, 13, False)}
    for name, (matrix, primaries, tc, full) in cases.items():
        planes = np.zeros((3, h, w), np.int64)
        expected = np.zeros((h, w, 3), np.int64)
        for y in range(h):
            for x in range(w):
                r, g, b = rgb[y, x]
                if matrix == 17:
                    # YCgCo-Ro (H.273, 2024): lossless lifting of 9-bit RGB into 10-bit planes
                    r9, g9, b9 = (int(round(v * 511)) for v in (r, g, b))
                    co = r9 - b9
                    t = b9 + (co >> 1)
                    cg = g9 - t
                    q = [t + (cg >> 1), cg + 512, co + 512]
                    out = [r9 / 511, g9 / 511, b9 / 511]
                elif matrix == 8:
                    # YCgCo in limited range: Y over 876 from 64, Cg and Co over 896 around 512
                    q = [round(64 + 876 * (r + 2 * g + b) / 4), round(512 + 896 * (2 * g - r - b) / 4), round(512 + 896 * (r - b) / 2)]
                    ey, cg, co = (q[0] - 64) / 876, (q[1] - 512) / 896, (q[2] - 512) / 896
                    out = [ey - cg + co, ey + cg, ey - cg - co]
                elif matrix == 0:
                    # Identity in limited range: the planes are G, B and R, each over 876 from 64
                    q = [round(64 + 876 * g), round(64 + 876 * b), round(64 + 876 * r)]
                    out = [(q[2] - 64) / 876, (q[0] - 64) / 876, (q[1] - 64) / 876]
                else:
                    ey, epb, epr = forward(matrix, primaries, tc, rgb[y, x])
                    q = [min(max(round(ey * 1023), 0), 1023), min(max(round(epb * 1023 + 512), 0), 1023), min(max(round(epr * 1023 + 512), 0), 1023)]
                    out = inverse(matrix, primaries, tc, q[0] / 1023, (q[1] - 512) / 1023, (q[2] - 512) / 1023)
                planes[:, y, x] = q
                expected[y, x] = to16(out)
        y4m = os.path.join(tmp, name + ".y4m")
        with open(y4m, "wb") as f:
            f.write(f"YUV4MPEG2 W{w} H{h} F1:1 Ip A1:1 C444p10 XYSCSS=444P10\nFRAME\n".encode())
            f.write(planes.astype("<u2").tobytes())
        obu = os.path.join(tmp, name + ".obu")
        run("aomenc", "--limit=1", "--lossless=1", "--profile=1", "--bit-depth=10", "--input-bit-depth=10", "--obu", "-o", obu, y4m)
        payload = heif.drop_temporal_delimiters(open(obu, "rb").read())
        item = heif.Item("av01", payload, [heif.ispe(w, h), heif.pixi([10, 10, 10]), heif.av1c_from_obus(payload, 10, sub_x=0, sub_y=0),
                                           heif.nclx(primaries, tc, matrix, full)], essential=[2])
        heif.write(os.path.join(folder, name + ".avif"), [item], item)
        write_png16(os.path.join(folder, name + ".png"), expected)


def gain_map_linear(tc, v):
    # libavif's gain map functions: PQ in extended SDR (1.0 = 203 nits), sRGB with its exact constants
    if tc == 16:
        return to_linear(16, v) * 10000 / 203
    if tc == 13:
        return 0.0 if v < 0 else v / 12.92 if v < 12.92 * 0.0030412825601275209 else ((v + 0.0550107189475866) / 1.0550107189475866) ** 2.4 if v < 1 else 1.0
    return to_linear(tc, v)


def gain_map_encoded(tc, l):
    if tc == 16:
        return to_encoded(16, min(max(l * 203 / 10000, 0.0), 1.0))
    if tc == 13:
        return 0.0 if l < 0 else l * 12.92 if l < 0.0030412825601275209 else 1.0550107189475866 * l ** (1 / 2.4) - 0.0550107189475866 if l < 1 else 1.0
    return to_encoded(tc, l)


# A gain map (ISO 21496-1) that avifenc cannot write from PNG files: an sRGB base, a half-size grey gain map, and a tmap item
# from base headroom 0 to alternate headroom 2 (4x), in an altr group with the base. The references are the formula of
# libavif's avifRGBImageApplyGainMap on the libavif decodes, at headroom 1 (weight 0.5) in PQ and 2 (weight 1) in sRGB.
def build_gain_map(here, tmp, photo):
    folder = os.path.join(here, "gainmap")
    os.makedirs(folder, exist_ok=True)
    photo(256, 256, 41).convert("RGB").save(os.path.join(tmp, "gm_base.png"))
    yy, xx = np.mgrid[0:128, 0:128]
    Image.fromarray((np.clip((xx + yy) / 254.0, 0, 1) * 255).astype(np.uint8), "L").save(os.path.join(tmp, "gm_gain.png"))
    run("avifenc", "-j", "4", "-q", "70", "-y", "420", "--cicp", "1/13/6", os.path.join(tmp, "gm_base.png"), os.path.join(tmp, "gm_base.avif"))
    run("avifenc", "-j", "4", "-q", "90", "-y", "400", "-r", "full", "--cicp", "1/8/0", os.path.join(tmp, "gm_gain.png"), os.path.join(tmp, "gm_gain.avif"))
    base = heif.read_avifenc(os.path.join(tmp, "gm_base.avif"))[0]
    gain = heif.read_avifenc(os.path.join(tmp, "gm_gain.avif"))[0]
    gain.properties = [p for p in gain.properties if p[4:8] != b"colr"]
    gmin, gmax, gamma, offset = (-1, 2), (2, 1), (1, 1), (1, 64)
    data = bytes([0]) + struct.pack(">HH", 0, 0) + bytes([0x40]) + struct.pack(">IIII", 0, 1, 2, 1)
    data += struct.pack(">iIiIIIiIiI", gmin[0], gmin[1], gmax[0], gmax[1], gamma[0], gamma[1], offset[0], offset[1], offset[0], offset[1])
    tmap = heif.Item("tmap", data, [heif.ispe(256, 256), heif.nclx(1, 16, 0, True)], references={"dimg": [base, gain]})
    heif.write(os.path.join(folder, "gainmap.avif"), [base, gain, tmap], base, groups=[("altr", [tmap, base])])

    def decode16(path):
        out = os.path.join(tmp, "decoded16.png")
        run("avifdec", "-d", "16", path, out)
        raw = subprocess.run(["ffmpeg", "-v", "error", "-i", out, "-f", "rawvideo", "-pix_fmt", "rgb48le", "-"], capture_output=True, check=True).stdout
        w, h = struct.unpack(">II", open(out, "rb").read(24)[16:24])
        return np.frombuffer(raw, "<u2").reshape(h, w, 3).astype(np.float64) / 65535

    b = decode16(os.path.join(tmp, "gm_base.avif"))
    g = decode16(os.path.join(tmp, "gm_gain.avif"))[..., 0]
    gy, gx = np.mgrid[0:256, 0:256]
    sx = np.clip((gx + 0.5) * 128 / 256 - 0.5, 0, 127)
    sy = np.clip((gy + 0.5) * 128 / 256 - 0.5, 0, 127)
    x0, y0 = sx.astype(int), sy.astype(int)
    x1, y1 = np.minimum(x0 + 1, 127), np.minimum(y0 + 1, 127)
    fx, fy = sx - x0, sy - y0
    gain_up = (g[y0, x0] * (1 - fx) + g[y0, x1] * fx) * (1 - fy) + (g[y1, x0] * (1 - fx) + g[y1, x1] * fx) * fy
    for headroom, tc in ((1, 16), (2, 13)):
        w = min(max(headroom / 2, 0), 1)
        log2 = (1 - gain_up) * (gmin[0] / gmin[1]) + gain_up * (gmax[0] / gmax[1])
        expected = np.zeros((256, 256, 3), np.int64)
        for c in range(3):
            lin = np.vectorize(lambda v: gain_map_linear(13, v))(b[..., c])
            out = (lin + 1 / 64) * np.power(2.0, log2 * w) - 1 / 64
            enc = np.vectorize(lambda v: gain_map_encoded(tc, v))(out)
            expected[..., c] = np.where(enc > 0, np.where(enc >= 1, 65535, np.floor(enc * 65535 + 0.5)), 0)
        write_png16(os.path.join(folder, f"gainmap.h{headroom}.tc{tc}.png"), expected)


# One AV1 stream in a plain track and in movie fragments (moof/traf/trun), muxed by ffmpeg with the avis brand; libavif
# 1.4 does not read the fragmented one. The two must decode to the same frames.
def build_fragments(here, tmp, photo):
    folder = os.path.join(here, "fragments")
    os.makedirs(folder, exist_ok=True)
    frames = os.path.join(tmp, "fragment%d.png")
    for i in range(6):
        photo(160, 96, 50 + i).convert("RGB").save(frames % i)
    y4m = os.path.join(tmp, "fragments.y4m")
    run("ffmpeg", "-v", "error", "-y", "-framerate", "10", "-i", frames, "-pix_fmt", "yuv420p", y4m)
    ivf = os.path.join(tmp, "fragments.ivf")
    run("aomenc", "--limit=6", "--end-usage=q", "--cq-level=30", "--lag-in-frames=4", "--ivf", "-o", ivf, y4m)
    run("ffmpeg", "-v", "error", "-y", "-i", ivf, "-c", "copy", "-brand", "avis", "-f", "mp4", os.path.join(folder, "plain.avif"))
    run("ffmpeg", "-v", "error", "-y", "-i", ivf, "-c", "copy", "-brand", "avis", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", os.path.join(folder, "fragmented.avif"))


# Files that must be refused with an UnsupportedImageFeatureException: an image with an unknown property marked essential
# (ISO/IEC 23008-12, 9.3.1), and one with matrix coefficients 15 (IPT-C2), which no decoder here implements
def build_unsupported(here, tmp):
    folder = os.path.join(here, "unsupported")
    os.makedirs(folder, exist_ok=True)
    item = heif.read_avifenc(os.path.join(here, "photo320_q75.avif"))[0]
    item.properties = item.properties + [heif.box("zzzz", b"\0\0\0\0")]
    item.essential = [len(item.properties) - 1]
    heif.write(os.path.join(folder, "essential_property.avif"), [item], item)
    item = heif.read_avifenc(os.path.join(here, "photo320_q75.avif"))[0]
    item.properties = [p for p in item.properties if p[4:8] != b"colr"] + [heif.nclx(1, 13, 15, True)]
    heif.write(os.path.join(folder, "matrix15.avif"), [item], item)


# Container features that avifenc does not write: an item whose data is the data of another item (iloc construction method
# 2), the layer selector (lsel) on a layered image, a thumbnail (thmb) and a depth map (an auxiliary image that is not alpha)
def build_container(here, tmp):
    folder = os.path.join(here, "container")
    os.makedirs(folder, exist_ok=True)
    photo_item = lambda: heif.read_avifenc(os.path.join(here, "photo320_q75.avif"))[0]

    hidden = photo_item()
    hidden.kind = "data"
    hidden.properties = []
    image = photo_item()
    image.data = b""
    image.source = hidden
    heif.write(os.path.join(folder, "item_from_item.avif"), [image, hidden], image)

    layered = heif.read_avifenc(os.path.join(here, "progressive.avif"))[0]
    layered.properties = [heif.ispe(128, 128) if p[4:8] == b"ispe" else p for p in layered.properties if p[4:8] != b"a1lx"] + [heif.lsel(0)]
    layered.essential = [len(layered.properties) - 1]
    heif.write(os.path.join(folder, "lsel_base.avif"), [layered], layered)

    image = photo_item()
    thumbnail = heif.read_avifenc(os.path.join(here, "tiny65x33.avif"))[0]
    thumbnail.references = {"thmb": [image]}
    depth = heif.read_avifenc(os.path.join(here, "yuv400.avif"))[0]
    depth.properties = depth.properties + [heif.auxc("urn:mpeg:mpegB:cicp:systems:auxiliary:depth")]
    depth.references = {"auxl": [image]}
    heif.write(os.path.join(folder, "thumbnail_depth.avif"), [image, thumbnail, depth], image)


# Files that must be refused for what they cost, not for what they hold: overlays nested 7 deep with 12 inputs each (35
# million decodes if each reference were followed), and a grid inside an overlay, whose canvas counts against the limit
def build_limits(here, tmp):
    folder = os.path.join(here, "limits")
    os.makedirs(folder, exist_ok=True)

    def overlay(children, width, height):
        data = bytes([0, 0]) + struct.pack(">HHHH", 0, 0, 0, 0xFFFF) + struct.pack(">HH", width, height)
        data += b"".join(struct.pack(">hh", 0, 0) for _ in children)
        return heif.Item("iovl", data, [heif.ispe(width, height)], references={"dimg": children})

    base = heif.read_avifenc(os.path.join(here, "tiny17x9.avif"))[0]
    items = [base]
    node = base
    for _ in range(7):
        node = overlay([node] * 12, 17, 9)
        items.append(node)
    heif.write(os.path.join(folder, "nested_overlays.avif"), items, node)

    tiles = heif.read_avifenc(os.path.join(here, "grid2x2_420.avif"))
    grid = heif.Item("grid", bytes([0, 0, 1, 1]) + struct.pack(">HH", 512, 384), [heif.ispe(512, 384)], references={"dimg": tiles})
    top = overlay([grid], 64, 64)
    heif.write(os.path.join(folder, "overlay_of_grid.avif"), [top, grid, *tiles], top)

    # The gain map of gainmap.avif with a gamma of 0, which libavif refuses
    data = open(os.path.join(here, "gainmap", "gainmap.avif"), "rb").read()
    gamma = struct.pack(">iIiIII", -1, 2, 2, 1, 1, 1)
    assert data.count(gamma) == 1
    open(os.path.join(folder, "gainmap_zero_gamma.avif"), "wb").write(data.replace(gamma, struct.pack(">iIiIII", -1, 2, 2, 1, 0, 1)))


# Alpha in limited range, which version 1.0.0 of AVIF allowed and avifenc does not write: the alpha of alpha420 coded
# losslessly by aomenc as a limited-range monochrome stream. libavif expands it, so its decode is the reference.
def build_limited_alpha(here, tmp):
    color = heif.read_avifenc(os.path.join(here, "alpha420.avif"))[0]
    alpha = np.asarray(Image.open(os.path.join(tmp, "rgba256.png")))[..., 3].astype(np.int64)
    limited = (16 + (alpha * 219 + 127) // 255).astype(np.uint8)
    y4m = os.path.join(tmp, "alpha_limited.y4m")
    with open(y4m, "wb") as f:
        f.write(b"YUV4MPEG2 W256 H256 F1:1 Ip A1:1 Cmono\nFRAME\n" + limited.tobytes())
    obu = os.path.join(tmp, "alpha_limited.obu")
    run("aomenc", "--limit=1", "--lossless=1", "--monochrome", "--obu", "-o", obu, y4m)
    payload = heif.drop_temporal_delimiters(open(obu, "rb").read())
    item = heif.Item("av01", payload, [heif.ispe(256, 256), heif.pixi([8]), heif.av1c_from_obus(payload, 8, mono=True),
                                       heif.auxc("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha")], essential=[2], references={"auxl": [color]})
    heif.write(os.path.join(here, "alpha_limited.avif"), [color, item], color)
