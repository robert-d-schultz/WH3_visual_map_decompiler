import numpy as np, sys
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

# Tree base name by tree map colour (colour_hex of the campaign_tree_ids DB table, 2026 update)
NAME_BY_HEX = {
    '007F01': 'tree_cwb_burn', '00C3BE': 'grass_tze', '191919': 'tree_large_evil', '285064': 'grass',
    '323232': 'tree_small_evil', '3278BE': 'tree_small_tze', '421453': 'rocks_small_def',
    '464646': 'grass_burnt', '4A904A': 'tree_large', '4B50AF': 'tree_large_tze', '4F8BD6': 'rocks_large_def',
    '644646': 'rocks_small_kho', '645A5A': 'shrubs_evil', '646446': 'shrubs_marsh', '646464': 'shrubs_burnt',
    '695596': 'shrubs_tze', '6E5A28': 'grass_nur', '6EC81E': 'tree_large_cth',
    '714BAF': 'tree_large_snow_pro_tze', '733246': 'grass_sla', '738723': 'shrubs_swamp',
    '787878': 'rocks_small', '7D0519': 'grass_grn', '7D6E6E': 'grass_evil', '823232': 'grass_kho',
    '82D7BE': 'tree_mangrove', '878773': 'rocks_small_ogr', '878787': 'rocks_large',
    '87914B': 'tree_large_jungle', '8B4800': 'tree_large_snow_pro', '8CD28C': 'tree_small',
    '8E6A00': 'tree_small_snow_pro', '9664C8': 'rocks_small_tze', '967355': 'tree_large_dead',
    '96735A': 'tree_large_mtn', '967D32': 'shrubs_nur', '96965A': 'tree_large_grn',
    '96F53C': 'tree_small_cth', '9772D5': 'tree_small_snow_pro_tze', 'A050C8': 'rocks_large_tze',
    'A0A087': 'rocks_large_ogr', 'A0C8BE': 'shrubs', 'A58228': 'tree_large_nur',
    'A996BC': 'grass_snow_pro_tze', 'AB9F82': 'tree_large_dead_burnt', 'AF9673': 'rocks_small_dkl',
    'B46EB4': 'grass_desert_def', 'B4BE64': 'tree_small_jungle', 'C3C373': 'tree_small_grn',
    'C6AC7B': 'grass_snow', 'C80A28': 'shrubs_grn', 'C86800': 'tree_large_snow', 'C89600': 'tree_small_snow',
    'C89664': 'grass_desert', 'C8AA82': 'rocks_large_dkl', 'CDD0BE': 'skull_mounds',
    'DDDDDD': 'tree_small_burnt', 'E6AFE6': 'shrubs_desert_def', 'E6E6FF': 'shrubs_snow',
    'E6F0C3': 'skull_mounds_light', 'F06EFF': 'tree_large_blossom', 'FAC85F': 'tree_small_palm',
    'FF5050': 'shrubs_kho', 'FF5AF0': 'grass_coral', 'FF7E00': 'grass_mtn', 'FF8A3C': 'shrubs_desert',
    'FFA050': 'shrubs_mtn', 'FFA0F0': 'shrubs_coral', 'FFAA00': 'tree_large_palm',
    'FFFFFF': 'tree_large_burnt',
}
NAME_BY_RGB = {(int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)): n for h, n in NAME_BY_HEX.items()}

def tif_index_to_name(path):
    im = Image.open(path)
    pal = im.getpalette()
    out = {}
    for i in range(256):
        rgb = tuple(pal[3*i:3*i+3])
        if rgb in NAME_BY_RGB:
            out[i] = NAME_BY_RGB[rgb]
    return im, out

if __name__ == '__main__':
    im, mapping = tif_index_to_name(sys.argv[1])
    a = np.array(im)
    used = set(int(v) for v in np.unique(a))
    print('indices used in image:', len(used))
    print('resolved via palette :', len(mapping))
    unresolved = sorted(used - set(mapping) - {255})
    print('used but unresolved  :', unresolved)
    pal = im.getpalette()
    for i in unresolved:
        print('   idx', i, 'rgb', tuple(pal[3*i:3*i+3]), '#%02x%02x%02x' % tuple(pal[3*i:3*i+3]))
    print()
    for i in sorted(used - {255}):
        print(i, mapping.get(i, '???'))
