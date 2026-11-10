"""Renders desktop/logo.svg at each icon size with Chromium and packs the PNGs into one .ico."""
import pathlib, struct, sys
from playwright.sync_api import sync_playwright

svg_path, ico_path, sheet = map(pathlib.Path, sys.argv[1:4])
svg = svg_path.read_text(encoding="utf-8")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

pngs = []
with sync_playwright() as p:
    b = p.chromium.launch()
    for n in SIZES:
        pg = b.new_page(viewport={"width": n, "height": n})
        art = svg
        if n <= 24:   # too small for the ring: the dot sits straight on the tile, a little bigger
            art = art.replace('r="22"', 'r="28"').replace('stroke-width="14"', 'stroke-width="0"')
        pg.set_content(f'<html><body style="margin:0;background:transparent">{art.replace("<svg ", f"<svg width={n} height={n} ", 1)}</body></html>')
        pngs.append(pg.screenshot(omit_background=True, clip={"x": 0, "y": 0, "width": n, "height": n}))
        pg.close()
    # A preview sheet on both grounds, to check the small sizes by eye.
    cells = "".join(f'<img src="data:image/png;base64,{__import__("base64").b64encode(d).decode()}" style="margin:8px;image-rendering:pixelated;width:{max(n, 32) * 2}px">' for n, d in zip(SIZES, pngs))
    pg = b.new_page(viewport={"width": 1500, "height": 700})
    pg.set_content(f'<body style="margin:0"><div style="background:#1a1b1f;padding:10px">{cells}</div><div style="background:#f5f5f7;padding:10px">{cells}</div></body>')
    pg.screenshot(path=str(sheet), full_page=True)
    b.close()

head = struct.pack("<HHH", 0, 1, len(pngs))
offset = 6 + 16 * len(pngs)
entries, blobs = b"", b""
for n, data in zip(SIZES, pngs):
    entries += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset + len(blobs))
    blobs += data
ico_path.write_bytes(head + entries + blobs)
print(ico_path, ico_path.stat().st_size)
