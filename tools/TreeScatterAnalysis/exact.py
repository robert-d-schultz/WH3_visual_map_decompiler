import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None
from parse import parse
from palette import tif_index_to_name

def run(tif, tl, label):
    im, idx2name = tif_index_to_name(tif); img = np.array(im); H, W = img.shape
    n2i = {v: k for k, v in idx2name.items()}
    magic, ver, ww, wh, groups = parse(tl)
    cc, cr = W//2, H//2; dx = ww/(cc-1); sz = wh/H; dz = 2*sz
    xs, zs, ex = [], [], []
    skip = 0
    for name, g in groups:
        if name[:-3] not in n2i: skip += len(g); continue
        xs.append(g['x'].astype(np.float64)); zs.append(g['z'].astype(np.float64))
        ex.append(np.full(len(g), n2i[name[:-3]]))
    x = np.concatenate(xs); z = np.concatenate(zs); e = np.concatenate(ex)
    i = np.clip(np.round(x/dx).astype(int), 0, cc-1)
    b = i & 1
    j = np.clip(np.round((z - b*sz)/dz).astype(int), 0, cr-1)
    r = 2*j + b
    col = 2*i; row = H-1-r
    good = (row >= 0) & (row < H)
    got = img[np.clip(row, 0, H-1), col]
    print('%-26s n=%7d skip=%5d   EXACT sampled pixel: %.6f' % (label, len(x), skip, (got == e).mean()))
    # residual check
    print('     jx %+.5f..%+.5f   jz %+.5f..%+.5f   (both should be exactly +-0.2)' % (
        (x-i*dx).min(), (x-i*dx).max(), (z-r*sz).min(), (z-r*sz).max()))

run("C:/Users/rob/Desktop/tree_probes/a_grass/wh3_main_combi_map_1.tree.1734296d3b60593.tif",
    "C:/Users/rob/Desktop/tree_probes/a_grass/trees.campaign_tree_list", 'probe A grass')
run("C:/Users/rob/Desktop/tree_probes/b_rocks_small/wh3_main_combi_map_1.tree.1734296d3b60593.tif",
    "C:/Users/rob/Desktop/tree_probes/b_rocks_small/trees.campaign_tree_list", 'probe B rocks_small')
run("C:/Users/rob/Desktop/tree_probes/c_tile4x4/wh3_main_combi_map_1.tree.1734296d3b60593.tif",
    "C:/Users/rob/Desktop/tree_probes/c_tile4x4/trees.campaign_tree_list", 'probe C tile4x4')
run("D:/SteamLibrary/steamapps/common/Total War WARHAMMER III/assembly_kit_tow/raw_data/terrain/campaigns/cr_oldworld_map_1/cr_oldworld_map_1.tree.186c366908be529.tif",
    "C:/Users/rob/Desktop/campaign_maps/cr_oldworld_map_1/display/trees/trees.campaign_tree_list", 'CA cr_oldworld_map_1')
