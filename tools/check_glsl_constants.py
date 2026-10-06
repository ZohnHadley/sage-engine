#!/usr/bin/env python3
"""Fails when a compiled OpenGL effect's shader has a gap in its constants.

MojoShader, which turns an effect's shaders into GLSL for MonoGame's OpenGL build, packs the constant
registers a shader uses into one array and drops the ones it does not; MonoGame fills that array by each
parameter's register. A parameter used only in part (a matrix whose last column the shader never reads)
leaves a gap inside it, and every constant after the gap is read from the wrong place: issue #481, where
the sun's shadow matrices shifted the pixel shader's colours and fog by two registers and the ground drew
black. Run it on a folder of .mgfxo files built with /Profile:OpenGL.

    python3 tools/check_glsl_constants.py src/Sage.Host/bin/Development/net8.0/Content/shaders
"""
import pathlib
import re
import struct
import sys


def shaders(data):
    """Each GLSL shader in an .mgfxo: its source follows its length as a little-endian int32."""
    for match in re.finditer(rb"#ifdef GL_ES", data):
        start = match.start()
        (length,) = struct.unpack("<i", data[start - 4:start])
        yield data[start:start + length].decode("latin-1")


def gaps(source):
    """(stage, register, slot) for each constant whose slot in the packed array is not its register."""
    for stage in ("ps", "vs"):
        for register, slot in re.findall(rf"#define {stage}_c(\d+) {stage}_uniforms_vec4\[(\d+)\]", source):
            if register != slot:
                yield stage, int(register), int(slot)


def main(folder):
    files = sorted(pathlib.Path(folder).glob("*.mgfxo"))
    if not files:
        print(f"no .mgfxo files in {folder}")
        return 1
    problems = 0
    for path in files:
        for index, source in enumerate(shaders(path.read_bytes())):
            found = list(gaps(source))
            if found:
                problems += 1
                stage, register, slot = found[0]
                print(f"{path.name}: shader {index} ({stage}) reads constant c{register} from slot {slot}: "
                      f"a parameter before it is only partly used, so {len(found)} constant(s) shift")
    print(f"{len(files)} effect(s) checked, {problems} shader(s) with gaps")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "."))
