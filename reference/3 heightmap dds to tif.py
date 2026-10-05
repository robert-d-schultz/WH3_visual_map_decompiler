from PIL import Image
import tifffile
import numpy as np

# This shit doesnt work, just copy+paste in photoshop from the dds to your tif


input_file = "D:\\SteamLibrary\\steamapps\\common\\Total War WARHAMMER III\\assembly_kit\\working_data\\terrain\\campaigns\\wh3_main_combi_map_1\\full_height_map.dds"
output_file = "./wh3_main_combi_map_1.height.180d2713c94c633.tif"


# Read the BC6H_SF16 compressed image data
dds_image = Image.open(input_file, formats=None)

print(type(dds_image))
print(dds_image.format)

# Extract the red channel
red_channel = dds_image.split()[0]

# Convert red channel to grayscale
red_grayscale = red_channel.convert("L")

# Create a new image with 32-bit float pixel format
float_image = red_grayscale.convert("F")

# Save the float image as TIF
float_image.save(output_file, "TIFF")