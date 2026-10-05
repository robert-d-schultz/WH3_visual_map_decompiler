import numpy as np
d = np.load('cells.npz')
JX, JZ = d['JX'], d['JZ']; M, N = JX.shape
tx = (JX+0.2)/0.4; tz = (JZ+0.2)/0.4
I = np.arange(N)[None, :]; J = np.arange(M)[:, None]
B = (I & 1)                      # sub-row bit
C = 2*I                          # pixel column
R = 2*J + B                      # pixel row
def cov(t, expr, name):
    ph = np.exp(2j*np.pi*(t - expr))
    c = np.mod(np.angle(ph.mean())/(2*np.pi), 1.0)
    err = np.abs(np.mod(t - expr - c + 0.5, 1.0) - 0.5)
    print('   %-34s |mean|=%.4f  within 1e-3: %.4f  1e-2: %.4f' % (name, abs(ph.mean()), (err < 1e-3).mean(), (err < 1e-2).mean()))
print('X field:')
cov(tx, 0.601355229*I + 0.244342*J, 'frac(a*i + g*j)')
cov(tx, 0.601355229*I + 0.744342*J, 'frac(a*i + (g+.5)*j)')
cov(tx, 0.3006776145*C + 0.244342*J, 'frac(a/2*col + g*j)')
for bb in (0.0, 0.25, 0.5, 0.75):
    cov(tx, 0.601355229*I + 0.244342*J + bb*B, 'frac(a*i + g*j + %.2f*b)' % bb)
print('Z field:')
cov(tz, 0.085032449*I + 0.686598*J, 'frac(a*i + g*j)')
cov(tz, 0.085032449*I + 0.186585*J, 'frac(a*i + (g-.5)*j)')
for bb in (0.0, 0.25, 0.5, 0.75):
    cov(tz, 0.085032449*I + 0.686598*J + bb*B, 'frac(a*i + g*j + %.2f*b)' % bb)
