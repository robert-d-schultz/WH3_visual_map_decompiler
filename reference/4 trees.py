import struct
from PIL import Image
import math

#Meh, this isn't perfect, likely never will be since I think there is randomness (there isn't?)


binary_file = "C:\\Users\\rob\\Desktop\\campaign_maps\\wh3_main_combi_map_4\\display\\trees\\trees.campaign_tree_list"
#binary_file = "D:\\SteamLibrary\\steamapps\\common\\Total War WARHAMMER III\\assembly_kit\\working_data\\campaign_maps\\wh3_main_combi_map_1\\display\\trees\\trees.campaign_tree_list"
indexed_tif_file = './tree_output.tif'

#tif_width = 3000;
tif_width = 2880;
tif_height = 1941;

order =  [ "grass_tze",
        "tree_large_evil",
        "grass",
        "tree_small_evil",
        "tree_small_tze",
        "rocks_small_def",
        "grass_burnt",
        "tree_large",
        "tree_large_tze",
        "rocks_large_def",
        "rocks_small_kho",
        "shrubs_evil",
        "shrubs_marsh",
        "shrubs_burnt",
        "shrubs_tze",
        "grass_nur",
        "tree_large_cth",
        "tree_large_snow_pro_tze",
        "grass_sla",
        "shrubs_swamp",
        "rocks_small",
        "grass_grn",
        "grass_evil",
        "grass_kho",
        "tree_mangrove",
        "rocks_small_ogr",
        "rocks_large",
        "tree_large_jungle",
        "tree_large_snow_pro",
        "tree_small",
        "tree_small_snow_pro",
        "rocks_small_tze",
        "tree_large_dead",
        "tree_large_mtn",
        "shrubs_nur",
        "tree_large_grn",
        "tree_small_cth",
        "tree_small_snow_pro_tze",
        "rocks_large_tze",
        "rocks_large_ogr",
        "shrubs",
        "tree_large_nur",
        "grass_snow_pro_tze",
        "tree_large_dead_burnt",
        "rocks_small_dkl",
        "grass_desert_def",
        "tree_small_jungle",
        "tree_small_grn",
        "grass_snow",
        "shrubs_grn",
        "tree_large_snow",
        "tree_small_snow",
        "grass_desert",
        "rocks_large_dkl",
        "skull_mounds",
        "tree_small_burnt",
        "shrubs_desert_def",
        "shrubs_snow",
        "skull_mounds_light",
        "tree_large_blossom",
        "tree_small_palm",
        "shrubs_kho",
        "grass_coral",
        "grass_mtn",
        "shrubs_desert",
        "shrubs_mtn",
        "shrubs_coral",
        "tree_large_palm",
        "tree_large_burnt" ]


# Create a new indexed image
indexed_image = Image.new('P', (tif_width, tif_height), 255)

# Set the color palette for the indexed image
color_palette = []
for i in range(256):
    # For simplicity, assuming that the same value in the DDS file
    # corresponds to the same indexed color in the TIF
    color_palette.extend([i, i, i])
    
indexed_image.putpalette(color_palette)

with open(binary_file, 'rb') as f:
    f.read(8) #magic number
    f.read(4) #version
    map_width = struct.unpack("<f", f.read(4))[0]
    map_height = struct.unpack("<f", f.read(4))[0]
    
    print(map_width)
    print(map_height)
    x_factor = (tif_width)/map_width
    z_factor = (tif_height)/map_height
    print(x_factor)
    print(z_factor)
    
    tree_type_count = struct.unpack("<L", f.read(4))[0]
    for _ in range(tree_type_count):
        tree_name_length = struct.unpack("<H", f.read(2))[0]
        tree_name = f.read(tree_name_length).decode('UTF-8')
        
        i = order.index(tree_name[:-3])
        
        tree_count = struct.unpack("<L", f.read(4))[0]
        for _ in range(tree_count):
            (x,y,z) = struct.unpack("<fff", f.read(12))
            f.read(3)
            
            x_ = x * x_factor
            z_ = z * z_factor

            x_rounded = math.floor(x_)
            z_rounded = math.floor(z_)
            
            
            # Set the pixel values in the indexed image
            #print(x,z)
            indexed_image.putpixel((x_rounded, z_rounded), i)
            try:
                indexed_image.putpixel((x_rounded+1, z_rounded), i)
            except:
                pass
            try:
                indexed_image.putpixel((x_rounded-1, z_rounded), i)
            except:
                pass
            try:
                indexed_image.putpixel((x_rounded, z_rounded+1), i)
            except:
                pass
            try:
                indexed_image.putpixel((x_rounded, z_rounded-1), i)
            except:
                pass
            #try:
            #    indexed_image.putpixel((x_rounded+1, z_rounded+1), i)
            #except:
            #    pass

# Save the indexed image as a TIF file
indexed_image.save(indexed_tif_file, format='TIFF')

