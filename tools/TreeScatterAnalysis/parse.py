import struct, sys, numpy as np
from collections import Counter

def parse(path):
    d = open(path,'rb').read()
    o = 0
    magic = d[0:8]; ver = struct.unpack_from('<I', d, 8)[0]
    w, h = struct.unpack_from('<ff', d, 12)
    o = 20
    n = struct.unpack_from('<I', d, o)[0]; o += 4
    groups = []
    for _ in range(n):
        ln = struct.unpack_from('<H', d, o)[0]; o += 2
        name = d[o:o+ln].decode('utf8'); o += ln
        c = struct.unpack_from('<I', d, o)[0]; o += 4
        arr = np.frombuffer(d, dtype=np.dtype([('x','<f4'),('y','<f4'),('z','<f4'),('b0','u1'),('b1','u1'),('b2','u1')]), count=c, offset=o)
        o += 15*c
        groups.append((name, arr))
    assert o == len(d), (o, len(d))
    return magic, ver, w, h, groups

if __name__ == '__main__':
    magic, ver, w, h, groups = parse(sys.argv[1])
    print('magic', magic.hex(), 'ver', ver, 'world', w, h, 'types', len(groups))
    tot = sum(len(a) for _,a in groups)
    print('total instances', tot)
    b0 = Counter(); b1 = Counter(); b2 = Counter()
    for _,a in groups:
        b0.update(a['b0'].tolist()); b1.update(a['b1'].tolist()); b2.update(a['b2'].tolist())
    print('b0', sorted(b0.items())[:20])
    print('b1', sorted(b1.items())[:20])
    print('b2', sorted(b2.items())[:20])
    print('first 12 groups:', [(n, len(a)) for n,a in groups[:12]])
    allx = np.concatenate([a['x'] for _,a in groups]); allz = np.concatenate([a['z'] for _,a in groups]); ally = np.concatenate([a['y'] for _,a in groups])
    print('x range', allx.min(), allx.max())
    print('y range', ally.min(), ally.max())
    print('z range', allz.min(), allz.max())
