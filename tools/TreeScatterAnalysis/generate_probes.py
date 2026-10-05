"""Build drop-in probe tree maps for BOB round-tripping.

Each probe clones the palette and pixel dimensions of the project's own tree
TIFF and keeps its exact filename, so it can replace the original in place.
"""
import numpy as np, os, sys
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

SRC = sys.argv[1]
OUT = sys.argv[2]
src = Image.open(SRC)
W, H = src.size
PAL = src.getpalette()
NAME = os.path.basename(SRC)
print('template %s  %dx%d' % (NAME, W, H))

def save(folder, arr, note):
    d = os.path.join(OUT, folder)
    os.makedirs(d, exist_ok=True)
    im = Image.fromarray(arr, mode='P'); im.putpalette(PAL)
    p = os.path.join(d, NAME)
    im.save(p, format='TIFF', compression='tiff_lzw')
    print('  %-16s %8.1f KiB  %s' % (folder, os.path.getsize(p)/1024, note))

save('a_grass', np.full((H, W), 2, np.uint8),
     'every pixel = grass (4 variants)')
save('b_rocks_small', np.full((H, W), 20, np.uint8),
     'every pixel = rocks_small (3 variants)')
TILE16 = [29, 27, 2, 46, 51, 50, 1, 7, 40, 3, 36, 20, 48, 16, 55, 32]
tile = np.array(TILE16, np.uint8).reshape(4, 4)
save('c_tile4x4', np.tile(tile, (H//4 + 1, W//4 + 1))[:H, :W],
     'repeating 4x4 tile of 16 distinct types')
