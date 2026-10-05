import numpy as np
d = np.load('cells.npz')
JX, JZ = d['JX'], d['JZ']; M, N = JX.shape
tx = (JX+0.2)/0.4; tz = (JZ+0.2)/0.4
A, B = 0.601355229, 0.085032449
LIM = 200; I = np.arange(LIM)
px = np.exp(2j*np.pi*(tx[:, :LIM] - I[None, :]*A)).mean(axis=1)
pz = np.exp(2j*np.pi*(tz[:, :LIM] - I[None, :]*B)).mean(axis=1)
clean = (np.abs(px) > 0.99) & (np.abs(pz) > 0.99)
print('clean rows: %d ; first 24 clean j: %s' % (clean.sum(), np.nonzero(clean)[0][:24].tolist()))
phx = np.mod(np.angle(px)/(2*np.pi), 1.0)
phz = np.mod(np.angle(pz)/(2*np.pi), 1.0)
cj = np.nonzero(clean)[0]
print('\nphase(x) for first 12 clean rows:', ' '.join('%.6f' % v for v in phx[cj[:12]]))
print('phase(z) for first 12 clean rows:', ' '.join('%.6f' % v for v in phz[cj[:12]]))
# find integer Delta with frac(Delta*A) = dphx and frac(Delta*B) = dphz
DMAX = 2_000_000
D = np.arange(DMAX)
fa = np.mod(D*A, 1.0); fb = np.mod(D*B, 1.0)
print('\nrow-to-row advance between consecutive clean rows:')
for t in range(8):
    j1, j2 = cj[t], cj[t+1]
    da = np.mod(phx[j2]-phx[j1], 1.0); db = np.mod(phz[j2]-phz[j1], 1.0)
    s = np.abs(np.mod(fa-da+0.5, 1.0)-0.5) + np.abs(np.mod(fb-db+0.5, 1.0)-0.5)
    k = int(np.argmin(s))
    print('  j %4d -> %4d (dj=%d): dphase=(%.5f, %.5f)  best Delta=%d err %.2e' % (j1, j2, j2-j1, da, db, k, s[k]))
