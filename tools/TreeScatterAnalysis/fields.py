import numpy as np
from parse import parse
d = np.load('cells.npz')
JX, JZ, RT, VR = d['JX'], d['JZ'], d['RT'], d['VR']
M, N = JX.shape; P = 0.4
for tag, A in (('JX', JX), ('JZ', JZ)):
    dv = np.mod(np.diff(A, axis=1) + P/2, P) - P/2
    med = np.median(dv)
    print('%s step per i = %+0.9f   |dev|<2e-4 for %.5f of all pairs' % (tag, med, (np.abs(dv-med) < 2e-4).mean()))
    print('    normalised (/0.4) = %+0.9f      as frac() increment = %0.9f' % (med/P, (med/P) % 1.0))

print('\nvariant, probe A (4 variants), cell rows 0..3, i=0..23:')
for j in range(4):
    print('  j=%d' % j, VR[j, :24].tolist())
print('  test variant == (s(j) - i) mod 4 :', end=' ')
s = (VR[:, 0].astype(int) + np.arange(N)[0]) % 4
pred = np.mod(VR[:, :1].astype(int) - np.arange(N)[None, :], 4)
print('%.6f' % (pred == VR).mean())
pred2 = np.mod(VR[:, :1].astype(int) - (np.arange(N)[None, :] - 1), 4)
print('  test with i-1 offset (i=0,1 share a value):     %.6f' % (pred2 == VR).mean())

# probe B: 3 variants
W, H = 2880, 1941
magic, ver, ww, wh, groups = parse("C:/Users/rob/Desktop/tree_probes/b_rocks_small/trees.campaign_tree_list")
dx = ww/(N-1); sz = wh/H; dz = 2*sz
x = np.concatenate([g['x'] for _, g in groups]).astype(np.float64)
z = np.concatenate([g['z'] for _, g in groups]).astype(np.float64)
vb = np.concatenate([np.full(len(g), int(n[-2:])-1) for n, g in groups])
i = np.round(x/dx).astype(int); b = i & 1
j = np.round((z - b*sz)/dz).astype(int)
o = np.argsort(j.astype(np.int64)*N + i)
VB = vb[o].reshape(M, N)
print('\nvariant, probe B (3 variants), cell rows 0..3, i=0..23:')
for jj in range(4):
    print('  j=%d' % jj, VB[jj, :24].tolist())
predb = np.mod(VB[:, :1].astype(int) - np.arange(N)[None, :], 3)
print('  test variant == (s(j) - i) mod 3 : %.6f' % (predb == VB).mean())
np.save('VB.npy', VB)

print('\nrotation RT, cell rows 0..2, i=0..39:')
for j in range(3):
    print('  j=%d' % j, RT[j, :40].tolist())
tx = (JX + 0.2)/0.4; tz = (JZ + 0.2)/0.4
for tag, t in (('jx', tx), ('jz', tz)):
    print('  RT == floor(6*%s) : %.6f' % (tag, (np.floor(6*t).astype(int) == RT).mean()))
