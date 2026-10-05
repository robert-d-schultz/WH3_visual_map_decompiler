import struct
import numpy as np
import math
from PIL import Image
from collections import defaultdict
import os

# Paths to the binary file and the output image file
#binary_file = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\3k_dlc07_main_map\\tile_list.bin"
#binary_file = "C:\\Program Files (x86)\\Steam\\SteamApps\\common\\Total War THREE KINGDOMS\\assembly_kit\\working_data\\terrain\\campaigns\\3k_dlc07_main_map\\tile_list.bin"
#binary_file = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\3k_dlc07_main_map\\global_map\\tile_list.bin"
binary_file = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\wh3_main_combi_map_1\\tile_list.bin"
output_file = "./tilemap_output.png"
climate_output_file = "./climatemap_output.png"

game = "wh3"


# Manually writing the colors here, you can get them from /terrain/tiles/campaign/_tile_database/_settings.bin
climate_colors = {
    "cold": (0, 85, 85),
    "arid": (255, 170, 0),
    "temperate": (0, 85, 0),
    "sub_tropical": (170, 170, 85),
    "default": (0, 0, 0),
}
tile_colors = {
    "generic": (150, 170, 100),
    "river": (0, 0, 255),
    "river_crossing": (127, 0, 255),
    "river_mouth": (204, 204, 255),
    "river_start": (180, 180, 255),
    "roads_paved": (93, 66, 24),
    "sea": (83, 141, 213),
    "sea_coast": (255, 255, 0),
    "mountains_subtropical": (83, 176, 33),
    "mountains_cold": (24, 32, 193),
    "mountains_temperate": (182, 146, 55),
    "blockout_cliff": (249, 173, 105),
    "blockout_cliff_ends": (159, 34, 42),
    "roads_tracks": (93, 0, 24),
    "roads_imperial": (193, 0, 24),
    "mountains_terrace_farm": (70, 58, 118),
    "mountains_yellow": (255, 255, 127),
    "river_crossing_imperial": (44, 6, 127),
    "river_crossing_track": (218, 67, 255),
    "lakes": (32, 234, 22),
    "salt_pools": (0, 128, 80),
    "generic_sea": (57, 113, 183),
    "mines": (250, 65, 45),
    "canal": (0, 0, 104),
    "canal_links": (0, 55, 208),
    "mountains_tea": (16, 255, 228),
    "beach": (255, 232, 5),
    "cliff_gen": (253, 3, 1),        #wh3
    "cliff_gen_ends": (84, 230, 84), #wh3
    "roads": (93, 66, 24),           #wh3
    "roads_light": (125, 100, 65),   #wh3
}
exclude_from_global_mesh = {  #not used yet
    "generic":        False,
    "river":          True,
    "river_crossing": True,
    "river_mouth":    True,
    "river_start":    True,
    "roads_paved":    True,
    "sea":            True,
    "sea_coast":      False,
    "mountains_subtropical":   False,
    "mountains_cold":          False,
    "mountains_temperate":     False,
    "blockout_cliff":          True,
    "blockout_cliff_ends":     True,
    "roads_tracks":            True,
    "roads_imperial":          True,
    "mountains_terrace_farm":  False,
    "mountains_yellow":        False,
    "river_crossing_imperial": True,
    "river_crossing_track": True,
    "lakes":                True,
    "salt_pools":           True,
    "generic_sea":          False,
    "mines":                True,
    "canal":                True,
    "canal_links":          True,
    "mountains_tea":        False,
    "beach":                False,
}


# Go through the tiles first and record their shapes and category
tile_directory= "C:\\Users\\rob\\Desktop\\terrain\\tiles\\campaign\\_tile_database\\tiles\\"

tile_paths = [file for file in os.listdir(tile_directory) if os.path.isfile(os.path.join(tile_directory, file))]

tile_infos = defaultdict(tuple)

for path in tile_paths:
    #print(path)
    with open(tile_directory + path, 'rb') as g:
        g.read(8)  # magic number
        version = struct.unpack("<H", g.read(2))[0]
        assert version > 2, f"wrong file version, {version} expecting greater than 5"
        
        name_length = struct.unpack("<H", g.read(2))[0]
        name = g.read(name_length).decode('UTF-8')
        category_length = struct.unpack("<H", g.read(2))[0]
        category = g.read(category_length).decode('UTF-8')
        print(name, category)
        
        mask_length = struct.unpack("<H", g.read(2))[0]
        mask = g.read(mask_length).decode('UTF-8')
        mask = [int(char) for char in mask]
        
        if ((version > 5) and (game == "3k")) or ((version > 7) and (game == "wh3")): #versions are fucked up between 3k and WH3....
            g.read(2) #0's
        
        tile_width = struct.unpack("<L", g.read(4))[0]
        assert tile_width <= 64, f"{tile_width}"
        tile_height = struct.unpack("<L", g.read(4))[0]
        assert tile_height <= 64, f"{tile_height}"
        
        #I dunno about this section
        g.read(21)
        if version < 7:
            g.read(9)
        
        for _ in range(8):
            some_string_length = struct.unpack("<H", g.read(2))[0]
            some_string = g.read(some_string_length).decode('UTF-8')
        
        tile_path_length = struct.unpack("<H", g.read(2))[0]
        assert tile_path_length > 0, f"tile_path_length is {tile_path_length}"
        tile_path = g.read(tile_path_length).decode('UTF-8')
        assert "tiles\campaign" in tile_path, f"tile_path is nonsense"
        
        tile_infos[tile_path] = (mask, tile_width, tile_height, category)
        #print(tile_path)



#Read the tile map .bin and create a .png from it
with open(binary_file, 'rb') as f:
    f.read(8)  # magic number
    version = struct.unpack("<H", f.read(2))[0]
    assert version == 1 or version == 2, f"wrong file version, {version} expecting 1 or 2"
    
    
    tilelist_version = struct.unpack("<H", f.read(2))[0]
    assert tilelist_version == 1, f"wrong tilelist version, {tilelist_version} expecting 1"
    
    tile_count = struct.unpack("<L", f.read(4))[0]
    tiles = []
    for _ in range(tile_count):
        tile_length = struct.unpack("<H", f.read(2))[0]
        tile = f.read(tile_length).decode('UTF-8')
        tiles.append(tile)
        
    climate_count = struct.unpack("<L", f.read(4))[0]
    climates = []
    for _ in range(climate_count):
        climate_length = struct.unpack("<H", f.read(2))[0]
        climate = f.read(climate_length).decode('UTF-8')
        climates.append(climate)
        
    
    f.read(7*4) #floats?
    
    map_width = struct.unpack("<L", f.read(4))[0]
    map_height = struct.unpack("<L", f.read(4))[0]
    hexmap_width = struct.unpack("<L", f.read(4))[0]
    hexmap_height = struct.unpack("<L", f.read(4))[0]
    
    f.read(4*4) #signed ints?
    
    map_width_again = struct.unpack("<L", f.read(4))[0]
    #assert map_width == map_width_again, f"{map_width}, {map_width_again}" #this isn't true in WH3
    map_height_again = struct.unpack("<L", f.read(4))[0]
    #assert map_height == map_height_again, f"{map_height}, {map_height_again}" #this isn't true in WH3
    
    f.read(1) #possible bool or padding

    
    #Initialize image data
    image_data = np.zeros((map_height, map_width, 3), dtype=np.uint8)
    climate_image_data = np.zeros((map_height, map_width, 3), dtype=np.uint8)
    
    #Tile Info
    tileinfo_count = struct.unpack("<L", f.read(4))[0]
    for i in range(tileinfo_count):
        one = struct.unpack("<H", f.read(2))[0]
        assert one == 1
        
        tile_index = struct.unpack("<L", f.read(4))[0]
        assert tile_index < tile_count
        
        climate_index = struct.unpack("<B", f.read(1))[0]
        assert climate_index < climate_count
        
        tile_position_x = struct.unpack("<H", f.read(2))[0]
        tile_position_y = struct.unpack("<H", f.read(2))[0]
        
        
        #Direction
        foobar = struct.unpack("<B", f.read(1))[0]
        dir = foobar & 0xF0
        otherthing = foobar & 0x0F
        if dir == 16:
            pass
        elif dir == 32:
            pass
        elif dir == 64:
            pass
        elif dir == 128:
            pass
        else:
            print(dir)
            raise("weird direction")
            
            
        #Perhaps flow direction for the river tiles...?
        if otherthing == 0: #almost always this
            pass
        elif otherthing == 1: #never this
            pass
        elif otherthing == 2: #never this
            pass
        elif otherthing == 4: #some rivers use this?
            pass
        elif otherthing == 8: #never this
            pass
        else:
            print(otherthing)
            raise("weird 'otherthing'")
            
        
        
        #Something with global tilemap vs normal tilemap
        #I'd guess that 0 means it's invisible and 7 means it is
        #   Based on the "exclude from global mesh" bool in the tile settings
        seven = struct.unpack("<B", f.read(1))[0]
        if (seven == 7): #Always this for normal tilemap
            pass
        elif (seven == 0): #Usually this for global tilemap, sometimes still 7 (for roads/rivers/cliffs)
            pass
        else:
            print(seven)
            raise("weird 'seven'")
            
            
            
        #Heights of the tile, probably for boundingbox/culling reasons
        f.read(4) #min height, float
        f.read(4) #max height, float
        
        
        
        
        
        #Color in the image
        tile_path = tiles[tile_index]
        tile_mask, tile_width, tile_height, tile_category = tile_infos[tile_path]
        
        if True: #("river" in tile_category) or ("canal" in tile_category):
                
            #process the mask
            if len(tile_mask) == 0:
                tile_mask = [1]*(tile_width*tile_height)
                
            mask_array = np.array(tile_mask).reshape((tile_height, tile_width))
            
            
            #mirroring??
            #I think we mirror on the y... because we mirror again to get back to the right orientation? I dunno... this is confusing
            mask_array = np.flipud(mask_array)
            
            #rotate
            if dir == 32:
                mask_array = np.rot90(mask_array)
            elif dir == 64:
                mask_array = np.rot90(mask_array, k=2)
            elif dir == 128:
                mask_array = np.rot90(mask_array, k=3)           
            
            
            tile_color = tile_colors[tile_category]
            
            climate = climates[climate_index]
            climate_color = climate_colors[climate]
            
            mask_height, mask_width = mask_array.shape

            for i in range(mask_width):
                for j in range(mask_height):
                    if mask_array[j, i] == 1:
                        try:
                            if ("sea" in tile_category) and not np.array_equal(image_data[tile_position_y + j, tile_position_x + i], (0, 0, 0)):
                                pass
                            else:
                                image_data[tile_position_y + j, tile_position_x + i] = tile_color
                                climate_image_data[tile_position_y + j, tile_position_x + i] = climate_color
                        except IndexError:
                            print(f"Tile at ({tile_position_x + i}, {tile_position_y + j}) is out of bounds")
        
        
        
        

    image = Image.fromarray(image_data, "RGB")
    climate_image = Image.fromarray(climate_image_data, "RGB")
    
    #crop to final size
    image = image.crop((0, 0, map_width, map_height))
    image = image.transpose(Image.FLIP_TOP_BOTTOM)
    climate_image = climate_image.crop((0, 0, map_width, map_height))
    climate_image = climate_image.transpose(Image.FLIP_TOP_BOTTOM)
    
    image.save(output_file)
    climate_image.save(climate_output_file)