import numpy as np
from PIL import Image
import pydds  # External library for proper DDS handling

# File paths
heightmap_dds = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\wh3_main_combi_map_1\\full_height_map.dds"
normal_map = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\wh3_main_combi_map_1\\lf_normal.dds"
output_tif = "./reconstructed_heightmap.tif"

# Load DDS heightmap with proper BC6H decompression
height_data = pydds.load(heightmap_dds)  # Ensure this library supports BC6H
height_red = height_data[:, :, 0].astype(np.float32)  # Extract red channel as float

# Load normal map
normal_data = pydds.load(normal_map)
norm_x = normal_data[:, :, 1].astype(np.float32)  # Green channel
norm_y = normal_data[:, :, 3].astype(np.float32)  # Alpha channel

# Flip normal map vertically since it's upside down
norm_x = np.flipud(norm_x)
norm_y = np.flipud(norm_y)

# Normalize normal map values from [0, 255] to [-1, 1]
norm_x = (norm_x / 127.5) - 1.0
norm_y = (norm_y / 127.5) - 1.0

# Compute finite differences to estimate current slopes
height_dx = np.zeros_like(height_red)
height_dy = np.zeros_like(height_red)
height_dx[:, :-1] = height_red[:, 1:] - height_red[:, :-1]
height_dy[:-1, :] = height_red[1:, :] - height_red[:-1, :]

# Compare estimated slopes to normal map and correct height values
correction_factor = 0.5  # Tuning parameter
height_detailed = height_red.copy()
height_detailed[:, :-1] += correction_factor * (norm_x[:, :-1] - height_dx[:, :-1])
height_detailed[:-1, :] += correction_factor * (norm_y[:-1, :] - height_dy[:-1, :])

# Save as 32-bit float TIFF
height_detailed = height_detailed.astype(np.float32)
Image.fromarray(height_detailed).save(output_tif)
