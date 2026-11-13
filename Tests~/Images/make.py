# Rebuilds the test images in this folder: procedural source pictures (so the files carry no third-party content), AVIF
# files from libavif's avifenc and aomenc, and the references decoded by libavif with dav1d (PNG, 8-bit RGB).
#
# Needs: Python 3 with numpy and Pillow, avifenc and avifdec (libavif 1.x built with aom and dav1d), aomenc.
# Run from anywhere: python3 make.py
import os, shutil, struct, subprocess, sys, tempfile
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
MUX = os.path.join(HERE, "mux.py")
# Files that the decoder must refuse with an UnsupportedImageFeatureException
UNSUPPORTED = os.path.join(HERE, "unsupported")


def noise(rng, h, w, cells):
    grid = rng.random((cells + 3, cells + 3)).astype(np.float32)
    return np.asarray(Image.fromarray(grid, "F").resize((w, h), Image.BICUBIC))


def photo(w, h, seed):
    """A photo-like picture: smooth colour fields, detail at several scales, hard edges, stripes and a fine texture."""
    rng = np.random.default_rng(seed)
    s = 2
    W, H = w * s, h * s
    img = np.zeros((H, W, 3), np.float32)
    for c in range(3):
        img[..., c] = (noise(rng, H, W, 2) * 0.55 + noise(rng, H, W, 8) * 0.25
                       + noise(rng, H, W, 32) * 0.12 + noise(rng, H, W, 128) * 0.08)
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32) / max(H, W)
    img += xx[..., None] * rng.uniform(-0.3, 0.3, 3) + yy[..., None] * rng.uniform(-0.3, 0.3, 3)
    angle = rng.uniform(0, np.pi)
    stripes = 0.5 + 0.5 * np.sin((xx * np.cos(angle) + yy * np.sin(angle)) * rng.uniform(40, 90))
    x0, y0 = int(W * 0.55), int(H * 0.1)
    img[y0:y0 + H // 3, x0:x0 + W // 3] = stripes[y0:y0 + H // 3, x0:x0 + W // 3, None] * rng.uniform(0.3, 1, 3)
    img[H * 2 // 3:, :W // 4] += (rng.random((H - H * 2 // 3, W // 4, 1)) - 0.5) * 0.25
    im = Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8), "RGB")
    d = ImageDraw.Draw(im)
    for _ in range(14):
        colour = tuple(int(v) for v in rng.integers(0, 256, 3))
        x, y = rng.integers(0, W), rng.integers(0, H)
        r = int(rng.integers(W // 40 + 1, W // 6 + 2))
        kind = rng.integers(0, 3)
        if kind == 0:
            d.ellipse([x - r, y - r, x + r, y + r], fill=colour)
        elif kind == 1:
            d.rectangle([x - r, y - r // 2, x + r, y + r // 2], fill=colour)
        else:
            d.line([x, y, x + int(rng.integers(-W // 2, W // 2)), y + int(rng.integers(-H // 2, H // 2))], fill=colour, width=max(1, r // 6))
    return im.resize((w, h), Image.LANCZOS).filter(ImageFilter.GaussianBlur(0.5))


def screen(w, h):
    """Screen content: flat colours, repeated tiles and text, which encoders code with palette and intra block copy."""
    im = Image.new("RGB", (w, h), (250, 250, 250))
    d = ImageDraw.Draw(im)
    colours = [(220, 40, 40), (40, 120, 220), (30, 30, 30), (250, 200, 0)]
    for y in range(0, h, 40):
        for x in range(0, w, 40):
            d.rectangle([x + 4, y + 4, x + 30, y + 30], fill=colours[(x // 40 + y // 40) % 4])
            d.text((x + 8, y + 10), "Ab", fill=(255, 255, 255))
    return im


# The body of the top-level mdat box, which holds the one item of an avifenc file
def mdat_payload(path):
    data = open(path, "rb").read()
    pos = 0
    while pos < len(data):
        size, kind = struct.unpack(">I4s", data[pos:pos + 8])
        if kind == b"mdat":
            return data[pos + 8:pos + size]
        pos += size
    sys.exit(f"no mdat box in {path}")


def run(*args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"failed: {' '.join(args)}\n{result.stdout}{result.stderr}")


def main():
    for name in os.listdir(HERE):
        if name.endswith((".avif", ".png")):
            os.remove(os.path.join(HERE, name))
    shutil.rmtree(UNSUPPORTED, ignore_errors=True)
    os.makedirs(UNSUPPORTED)

    tmp = tempfile.mkdtemp()
    src = lambda name: os.path.join(tmp, name + ".png")
    photo(720, 720, 1).save(src("photo720"))
    photo(320, 320, 1).save(src("photo320"))
    photo(720, 720, 2).save(src("photo720b"))
    photo(320, 320, 2).save(src("photo320b"))
    p256 = photo(256, 256, 3)
    p256.save(src("photo256"))
    p256.convert("L").save(src("gray256"))
    photo(512, 512, 4).save(src("photo512"))
    photo(512, 384, 5).save(src("photo512x384"))
    photo(2048, 2048, 6).save(src("photo2048"))
    photo(2050, 2049, 7).save(src("photo2050"))
    screen(256, 256).save(src("screen256"))
    for w, h in ((1, 1), (2, 3), (17, 9), (65, 33), (7, 130)):
        p256.resize((w, h), Image.LANCZOS).save(src(f"tiny{w}x{h}"))

    photo_args = ["-d", "8", "-y", "420", "--cicp", "1/1/1", "-r", "full", "-c", "aom"]
    cases = {
        # Photos as most AVIF files hold them: 8-bit 4:2:0, BT.709, full range
        "photo720_q30": photo_args + ["-s", "4", "-q", "30", src("photo720")],
        "photo720_q70": photo_args + ["-s", "6", "-q", "70", src("photo720b")],
        "photo320_q40": photo_args + ["-s", "4", "-q", "40", src("photo320")],
        "photo320_q75": photo_args + ["-s", "8", "-q", "75", src("photo320b")],
        # Chroma formats, bit depths, matrices and ranges
        "yuv444": ["-y", "444", "-q", "60", src("photo256")],
        "yuv422": ["-y", "422", "-q", "60", src("photo256")],
        "yuv400": ["-y", "400", "-q", "60", src("gray256")],
        "d10_420": ["-d", "10", "-y", "420", "-q", "60", src("photo256")],
        "d10_444": ["-d", "10", "-y", "444", "-q", "60", src("photo256")],
        "d12_420": ["-d", "12", "-y", "420", "-q", "60", src("photo256")],
        "d12_444": ["-d", "12", "-y", "444", "-q", "60", src("photo256")],
        "limited709": ["-r", "l", "--cicp", "1/13/1", "-q", "60", src("photo256")],
        "bt601": ["--cicp", "1/13/6", "-q", "60", src("photo256")],
        "bt2020": ["--cicp", "9/16/9", "-d", "10", "-q", "60", src("photo256")],
        "identity": ["-y", "444", "--cicp", "1/13/0", "-q", "70", src("photo256")],
        "lossless": ["-l", "-s", "8", src("photo256")],
        # Coding tools
        "tiles": ["-s", "6", "--tilerowslog2", "2", "--tilecolslog2", "2", "-q", "50", src("photo512")],
        "screen": ["-s", "4", "-q", "60", "-a", "tune-content=screen", src("screen256")],
        "screen_ibc": ["-s", "2", "-q", "60", "-a", "tune-content=screen", "-a", "enable-intrabc=1", "-a", "enable-palette=1", src("screen256")],
        "grain1": ["-q", "50", "-a", "film-grain-test=1", src("photo256")],
        "grain7": ["-q", "50", "-a", "film-grain-test=7", src("photo256")],
        "grain10": ["-q", "50", "-a", "film-grain-test=10", src("photo256")],
        "grain16_d10": ["-q", "50", "-d", "10", "-a", "film-grain-test=16", src("photo256")],
        "restoration": ["-s", "2", "-q", "40", "-a", "enable-restoration=1", src("photo256")],
        "nocdef": ["-q", "40", "-a", "enable-cdef=0", src("photo256")],
        "deltaq": ["-s", "4", "-q", "40", "-a", "deltaq-mode=1", "-a", "enable-chroma-deltaq=1", src("photo512")],
        "sb128": ["-s", "4", "-q", "40", "-a", "sb-size=128", src("photo512")],
        "qm": ["-s", "4", "-q", "40", "-a", "enable-qm=1", src("photo256")],
        "speed0": ["-s", "0", "-q", "45", src("photo256")],
        "odd512x384": ["-q", "50", src("photo512x384")],
        "big2048": ["-s", "9", "-q", "50", src("photo2048")],
        "big2050x2049": ["-s", "9", "-q", "50", src("photo2050")],
        # Sizes that do not fill a block
        "tiny1x1": ["-q", "60", src("tiny1x1")],
        "tiny2x3": ["-q", "60", src("tiny2x3")],
        "tiny17x9": ["-q", "60", src("tiny17x9")],
        "tiny65x33": ["-q", "60", src("tiny65x33")],
        "tiny7x130": ["-q", "60", src("tiny7x130")],
        "tiny17x9_422": ["-y", "422", "-q", "60", src("tiny17x9")],
        # Transformative properties: crop, then rotate, then mirror
        "irot1": ["--irot", "1", "-q", "60", src("photo512x384")],
        "irot2": ["--irot", "2", "-q", "60", src("photo512x384")],
        "irot3": ["--irot", "3", "-q", "60", src("photo512x384")],
        "imir0": ["--imir", "0", "-q", "60", src("photo512x384")],
        "imir1": ["--imir", "1", "-q", "60", src("photo512x384")],
        "clap": ["--crop", "64,32,320,240", "-q", "60", src("photo512x384")],
        "clap_irot1_imir0": ["--crop", "64,32,320,240", "--irot", "1", "--imir", "0", "-q", "60", src("photo512x384")],
    }
    for name, args in cases.items():
        run("avifenc", "-j", "4", *args, os.path.join(HERE, name + ".avif"))

    # Super-resolution is not reachable through avifenc's still-image path: encode one key frame with aomenc and wrap it
    y4m = os.path.join(tmp, "photo256.y4m")
    run("ffmpeg", "-loglevel", "error", "-y", "-i", src("photo256"), "-pix_fmt", "yuv420p", y4m)
    obu = os.path.join(tmp, "superres.obu")
    run("aomenc", "--limit=1", "--kf-max-dist=0", "--end-usage=q", "--cq-level=30", "--superres-mode=1",
        "--superres-kf-denominator=12", "--superres-denominator=12", "--obu", "-o", obu, y4m)
    run(sys.executable, MUX, obu, os.path.join(HERE, "superres.avif"), "256", "256")

    # A layered image: the base layer is a key frame at half size, the second layer an inter frame predicted from it. The
    # same stream with an a1op property that selects operating point 1, the base layer alone, decodes
    progressive = os.path.join(UNSUPPORTED, "progressive.avif")
    run("avifenc", "-j", "4", "--progressive", "-y", "420", "-q", "60", src("photo256"), progressive)
    obu = os.path.join(tmp, "progressive.obu")
    with open(obu, "wb") as f:
        f.write(mdat_payload(progressive))
    run(sys.executable, MUX, obu, os.path.join(HERE, "a1op_base.avif"), "128", "128", "8", "1")

    shutil.rmtree(tmp)

    # The references: libavif with dav1d, 8-bit RGB, with the crop, rotation and mirroring applied. The two big files have
    # none, to keep the repository small: the tests check them against a recorded hash only.
    for name in sorted(os.listdir(HERE)):
        if name.endswith(".avif") and not name.startswith("big"):
            path = os.path.join(HERE, name)
            run("avifdec", "-d", "8", "--png-compress", "9", path, path[:-5] + ".png")


if __name__ == "__main__":
    main()
