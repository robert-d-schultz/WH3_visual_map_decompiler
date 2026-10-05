from PIL import Image

# This works, but you need to manually update the color table in photoshop

def convert_dds_to_indexed_tif(dds_file, indexed_tif_file, is_16bit=False):
    # Open the DDS image file
    with open(dds_file, 'rb') as f:
        data = f.read()

    # Read the width and height from the DDS header
    width = int.from_bytes(data[16:20], byteorder='little')
    height = int.from_bytes(data[12:16], byteorder='little')

    # Extract the pixel data from the DDS file
    pixel_data = data[128:]

    # Adjust how pixel data is read based on whether it's 16-bit or 8-bit
    if is_16bit:
        # If 16-bit, each pixel uses 2 bytes
        # Convert 2-byte values to single indexes by using every 2nd byte (simplified)
        pixel_data = pixel_data[::2]
        
        #drop first byte
        #pixel_data = pixel_data[1::2]
    else:
        # For 8-bit, use pixel data as is
        pass

    # Create a new indexed image with the same size as the DDS image
    indexed_image = Image.new('P', (width, height))

    # Set the color palette for the indexed image
    color_palette = []
    for i in range(256):
        # For simplicity, assuming that the same value in the DDS file
        # corresponds to the same indexed color in the TIF
        color_palette.extend([i, i, i])

    indexed_image.putpalette(color_palette)

    # Set the pixel values in the indexed image
    indexed_image.putdata(pixel_data)

    # Save the indexed image as a TIF file
    indexed_image.save(indexed_tif_file, format='TIFF')

# Usage example
#dds_file_path = "D:\\SteamLibrary\\steamapps\\common\\Total War WARHAMMER III\\assembly_kit\\working_data\\terrain\\campaigns\\wh3_main_combi_map_1\\global_map\\global_blend.dds"
dds_file_path = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\wh3_main_combi_map_1\\global_map\\global_blend.dds"
#dds_file_path = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\phar_combi\\global_map\\global_blend.dds"
#dds_file_path = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\3k_dlc07_main_map\\global_map\\global_blend.dds"
indexed_tif_file_path = "./wh3_main_combi_map_1.blend.186c3668f46e527.tif"
convert_dds_to_indexed_tif(dds_file_path, indexed_tif_file_path, is_16bit=False)