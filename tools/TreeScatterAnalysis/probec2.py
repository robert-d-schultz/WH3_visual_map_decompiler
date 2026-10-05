import numpy as np, sys
from parse import parse
from palette import tif_index_to_name
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

tif, tl = sys.argv[1], sys.argv[2]
im, idx2name = tif_index_to_name(tif)
img = np.array(im); H, W = img.shape
name2idx = {v: k for k, v in idx2name.items()}
TILE16 = [29, 27, 2, 46, 51, 50, 1, 7, 40, 3, 36, 20, 48, 16, 55, 32]
tile = np.array(TILE16).reshape(4, 4)
pos = {int(tile[r, c]): (r, c) for r in range(4) for c in range(4)}
magic, ver, ww, wh, groups = parse(tl)
N, M = W//2, H//2
dx = ww/(N-1); dz = 2*wh/H; sz = wh/H; z0 = 0.25*dz

xs, zs, tr = [], [], []
for name, g in groups:
    b = name[:-3]
    if b not in name2idx or name2idx[b] not in pos: continue
    xs.append(g['x'].astype(np.float64)); zs.append(g['z'].astype(np.float64))
    tr.append(np.full(len(g), pos[name2idx[b]][0]))
x = np.concatenate(xs); z = np.concatenate(zs); tr = np.concatenate(tr)
jf = (z - z0)/dz; j = np.round(jf).astype(int)
ok = np.abs(jf - j) < 0.40
x, z, tr, j = x[ok], z[ok], tr[ok], j[ok]
rz = z - (j*dz + z0)

# k = sampled row-from-bottom minus 2j, determined from tile row (image row from top)
# row_top = H-1-(2j+k)  =>  row_top%4 == (1940-2j-k)%4
k = np.full(len(j), -9)
for cand in (-1, 0, 1, 2):
    m = ((1940 - 2*j - cand) % 4) == tr
    k[m & (k == -9)] = cand
print('k distribution:', np.bincount(k[k >= 0], minlength=3).tolist(), ' unresolved:', int((k == -9).sum()))
print('k=-1 count:', int((k == -1).sum()))

print('\nrz/sz vs k (sz = one pixel row):')
t = rz/sz
for cand in sorted(set(k.tolist())):
    m = k == cand
    if m.sum() == 0: continue
    print('  k=%+d  n=%7d  rz/sz: min %+.4f  max %+.4f  mean %+.4f' % (cand, m.sum(), t[m].min(), t[m].max(), t[m].mean()))
