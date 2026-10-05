import numpy as np
d = np.load('cells.npz')
JX, JZ = d['JX'], d['JZ']; M, N = JX.shape
tx = (JX+0.2)/0.4; tz = (JZ+0.2)/0.4
# restrict to low i and low j for best float32 precision
sub = (slice(0, 200), slice(0, 300))
dtx = np.mod(np.diff(tx[sub], axis=1), 1.0).ravel()
dtz = np.mod(np.diff(tz[sub], axis=1), 1.0).ravel()
key = np.round(dtx, 4)*10000 + np.round(dtz, 4)
u, inv, c = np.unique(np.round(np.stack([np.round(dtx,4), np.round(dtz,4)]).T, 4), axis=0, return_inverse=True, return_counts=True)
o = np.argsort(-c)
print('most common (dtx, dtz) pairs out of %d samples:' % len(dtx))
tot = 0
for t in o[:14]:
    print('   dtx=%.4f  dtz=%.4f   n=%6d  (%.3f)' % (u[t,0], u[t,1], c[t], c[t]/len(dtx)))
    tot += c[t]
print('   top-14 cover %.3f' % (tot/len(dtx)))
a, b = np.load('ab.npy')
print('\nalpha=%.9f  beta=%.9f' % (a % 1, b))
print('frac(n*alpha), frac(n*beta) for n=1..8:')
for n in range(1, 9):
    print('   n=%d  %.4f  %.4f' % (n, (n*a) % 1, (n*b) % 1))
