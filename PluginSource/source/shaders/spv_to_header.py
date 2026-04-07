"""
Converts a SPIR-V binary (.spv) to a C uint32_t array for embedding in C++ code.

Usage:
    python spv_to_header.py sphIntegrate.spv
    python spv_to_header.py sphIntegrate.spv -o output.txt
    python spv_to_header.py sphIntegrate.spv --name kIntegrateShaderSpirv
"""

import sys
import argparse
from pathlib import Path

def convert_spv(input_path, output_path, array_name):
    data = Path(input_path).read_bytes()

    # Pad to 4-byte alignment
    while len(data) % 4 != 0:
        data += b'\x00'

    words = [int.from_bytes(data[i:i+4], 'little') for i in range(0, len(data), 4)]

    lines = [f"static const uint32_t {array_name}[] = {{"]
    for i in range(0, len(words), 8):
        chunk = ", ".join(f"0x{w:08X}" for w in words[i:i+8])
        lines.append(f"    {chunk},")
    lines.append(f"}}; // {len(words)} words, {len(data)} bytes — compiled from {Path(input_path).name}")

    result = "\n".join(lines)

    if output_path:
        Path(output_path).write_text(result)
        print(f"Written to {output_path}")
    else:
        # Default: same name as input but .txt
        out = Path(input_path).with_suffix(".txt")
        out.write_text(result)
        print(f"Written to {out}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Convert .spv to C uint32_t array")
    parser.add_argument("input", help="Path to .spv file")
    parser.add_argument("-o", "--output", help="Output text file path (default: <input>.txt)")
    parser.add_argument("--name", default="kComputeShaderSpirv", help="C array variable name (default: kComputeShaderSpirv)")
    args = parser.parse_args()

    convert_spv(args.input, args.output, args.name)
