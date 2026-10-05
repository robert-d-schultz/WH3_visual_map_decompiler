import struct
import numpy as np
import math
from PIL import Image

# Paths to the binary file and the output image file
#binary_file = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\3k_dlc07_main_map\\lf_sea_height_map.compressed_map"
binary_file = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\wh3_main_combi_map_1\\full_logic_map.compressed_map"
#binary_file = "C:\\Program Files (x86)\\Steam\\SteamApps\\common\\Total War THREE KINGDOMS\\assembly_kit\\working_data\\terrain\\campaigns\\3k_dlc07_main_map\\lf_sea_height_map.compressed_map"
#binary_file = "C:\\Users\\rob\\Desktop\\terrain\\campaigns\\3k_dlc07_main_map\\height_patches\\river_0_patch_0x0.compressed_map"
output_file = './heightmap_output.png'

with open(binary_file, 'rb') as f:
    f.read(8)  # magic number
    version = struct.unpack("<H", f.read(2))[0]
    assert version == 3, f"Version is {version}, expected 3"
    map_width = struct.unpack("<L", f.read(4))[0]
    map_height = struct.unpack("<L", f.read(4))[0]
    block_width = struct.unpack("<L", f.read(4))[0]
    block_height = struct.unpack("<L", f.read(4))[0]
    f.read(24)
    TABLE_INDEXED_length = struct.unpack("<H", f.read(2))[0]
    TABLE_INDEXED = f.read(TABLE_INDEXED_length).decode('UTF-8')


    offsets = []
    
    offset_count = struct.unpack("<L", f.read(4))[0]
    #f.seek(offset_count * 4, 1)  # Skip offsets
    for _ in range(offset_count):
        offset = struct.unpack("<L", f.read(4))[0]
        offsets.append(offset)
        
        
    lengths = []
        
    length_count = struct.unpack("<L", f.read(4))[0]
    assert offset_count == length_count
    #f.seek(length_count * 2, 1)  # Skip lengths
    for _ in range(length_count):
        length = struct.unpack("<H", f.read(2))[0]
        lengths.append(length)
        

    # Initialize an empty array for storing image data
    image_data = np.empty((0, math.ceil(map_width/block_width) * block_width), dtype='<H')

    row = np.empty((block_height, 0), dtype='<H')
    
    f.read(4) #block_table_length
    main_block_start = f.tell()
    
    block = []
    for i in range(offset_count):
    #for i in range(30):
        #print(f.tell())
        f.seek(offsets[i] + main_block_start) # I dunno, WH3's compressed map has padding sometimes or something
        #assert f.tell() == (offsets[i] + main_block_start), f"{f.tell()} and {offsets[i] + main_block_start}"
        #print(f"block {i}")
        unique_count = struct.unpack("<B", f.read(1))[0]
        #print(unique_count)
        
        if (unique_count == 143): #Uncompressed block (or possibly 16bit delta compression)
            bs = struct.unpack("<256H", f.read(512))
            block.extend(bs)
            
            
        elif (unique_count >= 128): #Delta compression
            bits_per_pixel = unique_count - 127
            
            # Read the base height value (16-bit signed integer)
            base_height_data = f.read(2)
            base_height = struct.unpack('<H', base_height_data)[0]

            # Calculate the total number of bits we need to read (256 pixels)
            total_bits = block_width*block_height * bits_per_pixel
            total_bytes = (total_bits + 7) // 8  # Round up to the nearest byte

            # Read the compressed data
            compressed_data = f.read(total_bytes)

            # Convert compressed data to a bit stream
            bitstream = int.from_bytes(compressed_data, byteorder='little')

            # Mask to extract the correct number of bits for each pixel
            mask = (1 << bits_per_pixel) - 1

            # Unpack each pixel's delta from the bitstream
            for i in range(256):  # 16x16 block = 256 pixels
                # Extract the next `bits_per_pixel` bits from the bitstream
                delta = bitstream & mask
                bitstream >>= bits_per_pixel  # Shift right to process the next set of bits

                # Add the delta to the base height to get the pixel value
                pixel_value = base_height + delta
                block.append(pixel_value)
            
            
        elif (unique_count < 128): #Table compression thing
            unique_values = []
            for _ in range(unique_count + 1): #One more for some reason
                unique_values.append(struct.unpack("<H", f.read(2))[0])
                
            # Determine how many bits are needed for each index
            bits_per_index = math.ceil(math.log2(unique_count + 1))
            
            # Calculate how many indices there are (256 total values in a 16x16 block)
            total_indices = block_width*block_height
            
            # Read the remaining part of the block which contains indices
            # We need to read enough bytes to cover all indices
            total_bits = total_indices * bits_per_index
            total_bytes = math.ceil(total_bits / 8)
            raw_index_data = f.read(total_bytes)
            
            
            # Now extract the individual indices using bit-packing
            indices = []
            bit_buffer = 0
            bits_in_buffer = 0
            byte_index = 0
            
            for _ in range(total_indices):
                # Ensure the bit buffer contains enough bits
                while bits_in_buffer < bits_per_index:
                    bit_buffer |= raw_index_data[byte_index] << bits_in_buffer
                    bits_in_buffer += 8
                    byte_index += 1
                
                # Extract the next index
                index = bit_buffer & ((1 << bits_per_index) - 1)
                assert index < (unique_count + 1), f"{index} in {unique_count+1}"
                indices.append(index)
                
                # Shift the buffer and decrease bit count
                bit_buffer >>= bits_per_index
                bits_in_buffer -= bits_per_index
                
            
            # Now use the indices to reconstruct the final 16x16 block
            block = [unique_values[index] for index in indices]
        else:
             raise Exception(f"I dunno what to do with a control byte of {unique_count}....") 
            
            
        if len(block) == (block_width * block_height):
            arr = np.array(block, dtype='<H')
            reshaped = arr.reshape((block_height, block_width))
            row = np.hstack((row.copy(), reshaped.copy()))
            
            if row.shape[1] >= map_width:
                image_data = np.vstack((image_data.copy(), row.copy()))
                row = np.empty((block_height, 0), dtype='<H')
            
            block = []

    image = Image.fromarray(image_data, "I;16")
    
    #crop to final size
    image = image.crop((0, 0, map_width, map_height))
    
    image.save(output_file)