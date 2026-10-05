"""Build the exact per-cell table (i,j) -> jx, jz, rotation, variant from probe A."""
import numpy as np, sys
from parse import parse
W, H = 2880, 1941
magic, ver, ww, wh, groups = parse("C:/Users/rob/Desktop/tree_probes/a_grass/trees.campaign_tree_list")
N, M = W//2, H//2
dx = ww/(N-1); sz = wh/H; dz = 2*sz
x = np.concatenate([g['x'] for _, g in groups]).astype(np.float64)
z = np.concatenate([g['z'] for _, g in groups]).astype(np.float64)
rot = np.concatenate([g['b1'] for _, g in groups]).astype(np.int8)
var = np.concatenate([np.full(len(g), int(n[-2:])-1, np.int8) for n, g in groups])
i = np.round(x/dx).astype(np.int32); b = (i & 1)
j = np.round((z - b*sz)/dz).astype(np.int32)
assert i.min() == 0 and i.max() == N-1 and j.min() == 0 and j.max() == M-1
lin = j.astype(np.int64)*N + i
assert len(np.unique(lin)) == N*M, 'cells not unique'
o = np.argsort(lin)
JX = (x - i*dx)[o].reshape(M, N)
JZ = (z - (2*j + b)*sz)[o].reshape(M, N)
RT = rot[o].reshape(M, N); VR = var[o].reshape(M, N)
XR = x[o].reshape(M, N); ZR = z[o].reshape(M, N)
np.savez('cells.npz', JX=JX, JZ=JZ, RT=RT, VR=VR, X=XR, Z=ZR, dx=dx, sz=sz, ww=ww, wh=wh, N=N, M=M)
print('cells.npz: %dx%d grid' % (M, N))
print('jx %.6f..%.6f   jz %.6f..%.6f' % (JX.min(), JX.max(), JZ.min(), JZ.max()))
print()
print('i=0 column (x IS the jitter, full float32 precision) -- first 16 j:')
print('  ', ' '.join('%+0.8f' % v for v in XR[:8, 0]))
print('  ', ' '.join('%+0.8f' % v for v in XR[8:16, 0]))
print('j=0 row, even i (z IS the jitter) -- first 16:')
print('  ', ' '.join('%+0.8f' % v for v in ZR[0, 0:16:2]))
