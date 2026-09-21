r"""Cut the stitched continent PNGs back into the per-ADT tiles the viewer reads.

The download link in the README is dead, so the tiles have to come from the client. They
already do, most of the way: WoWTools.MinimapExtract + translate_minimaps.py + MinimapCompile
turn the MPQs into one big PNG per map, and that output is what most people keep, because the
loose BLPs are twenty times the size and nothing reads them.

MinimapCompile crops to the bounding box of the tiles that exist and says nothing about where
that box sits, so the PNG alone cannot be placed on the world grid. md5translate.trs supplies
the missing half: it lists every tile the client ships, by map and by ADT coordinate, and its
bounding box reproduces all 63 compiled image sizes to the pixel. Extract it with

    MPQEditor.exe extract <client>\Data\patch-3.MPQ textures\Minimap\md5translate.trs <dir> /fp

map.csv is written from Map.dbc, so the ids are the client's own and not a hand-kept list.
"""

import argparse
import csv
import os
import re
import struct
import sys

TILE_RE = re.compile(r"map(\d+)_(\d+)\.blp$", re.IGNORECASE)


def read_trs(path):
    """{map directory: {(adt_x, adt_y)}} for every terrain tile the client ships."""
    blocks, cur = {}, None
    with open(path, encoding="utf-8", errors="ignore") as fh:
        for raw in fh:
            line = raw.rstrip("\r\n")
            if not line.strip():
                continue
            if line.startswith("dir: "):
                cur = line[5:].strip()
                blocks.setdefault(cur, set())
                continue
            if cur is None:
                continue
            parts = line.split("\t")
            if len(parts) != 2:
                parts = line.split()
            if len(parts) != 2:
                continue
            m = TILE_RE.match(parts[0].replace("\\", "/").split("/")[-1])
            if m:
                blocks[cur].add((int(m.group(1)), int(m.group(2))))
    return {k: v for k, v in blocks.items() if v}


def read_map_dbc(path):
    """[(id, directory, name)] from Map.dbc. WDBC, fixed records, strings in a trailing block."""
    data = open(path, "rb").read()
    magic, n_rec, n_fld, rec_size, _ = struct.unpack("<4sIIII", data[:20])
    if magic != b"WDBC":
        raise SystemExit("%s is not a WDBC file" % path)
    base = 20
    strings = base + n_rec * rec_size

    def text(off):
        end = data.index(b"\0", strings + off)
        return data[strings + off:end].decode("utf-8", "replace")

    out = []
    for i in range(n_rec):
        f = struct.unpack("<%dI" % n_fld, data[base + i * rec_size: base + (i + 1) * rec_size])
        out.append((f[0], text(f[1]), text(f[5])))
    return out


def png_size(path):
    with open(path, "rb") as fh:
        head = fh.read(24)
    return struct.unpack(">II", head[16:24])


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--trs", required=True, help="md5translate.trs extracted from the client")
    ap.add_argument("--compiled", required=True, help="folder of <MapDirectory>.png")
    ap.add_argument("--dbc", required=True, help="Map.dbc from the client")
    ap.add_argument("--out", required=True, help="target world/minimaps folder")
    ap.add_argument("--res", type=int, default=256, help="tile pixels, must match the compile")
    ap.add_argument("--quality", type=int, default=82, help="webp quality")
    ap.add_argument("--only", help="comma separated map directories, for a quick trial")
    ap.add_argument("--force", action="store_true", help="rewrite tiles that already exist")
    args = ap.parse_args()

    try:
        from PIL import Image
    except ImportError:
        raise SystemExit("needs Pillow: python -m pip install pillow")
    Image.MAX_IMAGE_PIXELS = None  # the continents are far past the decompression-bomb guard

    tiles = read_trs(args.trs)
    maps = read_map_dbc(args.dbc)
    wanted = set(args.only.split(",")) if args.only else None

    os.makedirs(args.out, exist_ok=True)
    rows, done, written, skipped = [], 0, 0, []

    for map_id, directory, name in sorted(maps):
        png = os.path.join(args.compiled, directory + ".png")
        if not directory or not os.path.exists(png):
            skipped.append((map_id, directory, name))
            continue
        rows.append((map_id, directory, name))
        if wanted and directory not in wanted:
            continue

        have = tiles.get(directory)
        if not have:
            print("[skip] %s has a PNG but no tiles in the trs" % directory)
            continue

        min_x, min_y = min(x for x, _ in have), min(y for _, y in have)
        max_x, max_y = max(x for x, _ in have), max(y for _, y in have)
        expect = ((max_x - min_x + 1) * args.res, (max_y - min_y + 1) * args.res)
        actual = png_size(png)
        if expect != actual:
            print("[skip] %s is %dx%d, the trs says %dx%d - different extraction?"
                  % (directory, actual[0], actual[1], expect[0], expect[1]))
            continue

        target = os.path.join(args.out, directory)
        os.makedirs(target, exist_ok=True)
        todo = [t for t in sorted(have)
                if args.force or not os.path.exists(
                    os.path.join(target, "map%02d_%02d.webp" % t))]
        if not todo:
            print("[have] %-26s %4d tiles" % (directory, len(have)))
            done += 1
            continue

        print("[cut ] %-26s %4d tiles, origin %d,%d" % (directory, len(todo), min_x, min_y))
        with Image.open(png) as img:
            img = img.convert("RGB")
            for tx, ty in todo:
                left, top = (tx - min_x) * args.res, (ty - min_y) * args.res
                img.crop((left, top, left + args.res, top + args.res)).save(
                    os.path.join(target, "map%02d_%02d.webp" % (tx, ty)),
                    "WEBP", quality=args.quality, method=4)
                written += 1
        done += 1

    # map.csv sits next to the executable, two levels up from world/minimaps
    csv_path = os.path.normpath(os.path.join(args.out, "..", "..", "map.csv"))
    with open(csv_path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["id", "directory", "name"])
        w.writerows(rows)

    print("\n%d maps cut, %d tiles written" % (done, written))
    print("map.csv lists %d maps with art, at %s" % (len(rows), csv_path))
    if skipped:
        print("%d maps have no compiled PNG (interiors are WMO minimaps, not a tile grid):"
              % len(skipped))
        for map_id, directory, name in skipped[:8]:
            print("    %-5d %-24s %s" % (map_id, directory, name))
        if len(skipped) > 8:
            print("    ... and %d more" % (len(skipped) - 8))
    return 0


if __name__ == "__main__":
    sys.exit(main())
