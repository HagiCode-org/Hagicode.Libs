#!/usr/bin/env python3
"""Checks the native layout of HagiCode.Libs.Imaging.

Modes:
  nupkg   <path.nupkg>      The package carries exactly one shared libvips library per supported RID under
                            runtimes/<rid>/native, ships only shared libraries, has no executables and no tools/ folder,
                            and includes README.md, icon.png and THIRD-PARTY-NOTICES.md at the root.
  publish <dir> <rid>       A publish for one RID contains only that RID's libvips library.

Exits 0 when every check passes, 1 otherwise. Standard library only, so it runs on every CI runner.
"""
import os
import sys
import zipfile

NATIVE_FILES = {
    "win-x64": "libvips-42.dll",
    "linux-x64": "libvips.so.42",
    "osx-x64": "libvips.42.dylib",
    "osx-arm64": "libvips.42.dylib",
}
REQUIRED_ROOT_FILES = ("README.md", "icon.png", "THIRD-PARTY-NOTICES.md")


def shared_library_problem(name, data):
    """Returns None when the bytes are a shared library (ELF ET_DYN, PE DLL or Mach-O dylib), else a reason."""
    head = data[:4096]
    if head[:4] == b"\x7fELF":
        if len(head) < 18:
            return "truncated ELF header"
        e_type = int.from_bytes(head[16:18], "little")
        if e_type != 3:
            return f"ELF e_type {e_type} is not a shared object (ET_DYN)"
        # PIE executables are also ET_DYN; only they carry a PT_INTERP program header naming a dynamic linker.
        if head[4] == 2:  # 64-bit
            phoff = int.from_bytes(head[32:40], "little")
            phentsize = int.from_bytes(head[54:56], "little")
            phnum = int.from_bytes(head[56:58], "little")
            for index in range(phnum):
                start = phoff + index * phentsize
                if start + 4 > len(head):
                    break
                if int.from_bytes(head[start:start + 4], "little") == 3:  # PT_INTERP
                    return "ELF has a PT_INTERP header, so it is an executable (PIE), not a shared library"
        return None
    if head[:2] == b"MZ":
        lfanew = int.from_bytes(head[0x3C:0x40], "little")
        if head[lfanew:lfanew + 4] != b"PE\x00\x00":
            return "MZ file without a PE signature"
        characteristics = int.from_bytes(head[lfanew + 22:lfanew + 24], "little")
        return None if characteristics & 0x2000 else "PE image is not a DLL"
    if head[:4] in (b"\xcf\xfa\xed\xfe",):
        filetype = int.from_bytes(head[12:16], "little")
        return None if filetype == 6 else f"Mach-O filetype {filetype} is not MH_DYLIB (6)"
    return "not a recognized shared library format (ELF, PE or Mach-O)"


def check_nupkg(path):
    errors = []
    with zipfile.ZipFile(path) as archive:
        names = [n for n in archive.namelist() if not n.endswith("/")]
        for name in names:
            if name.startswith("runtimes/") and "/native/" in name:
                problem = shared_library_problem(name, archive.read(name))
                if problem:
                    errors.append(f"{name}: {problem}")
    for rid, library in NATIVE_FILES.items():
        prefix = f"runtimes/{rid}/native/"
        found = sorted(os.path.basename(n) for n in names if n.startswith(prefix))
        if found != [library]:
            errors.append(f"{rid}: expected exactly [{library}] under {prefix}, found {found}")
    for name in names:
        lowered = name.lower()
        if lowered.endswith(".exe"):
            errors.append(f"executable in package: {name}")
        if name.startswith("tools/"):
            errors.append(f"tools/ folder in package: {name}")
    for required in REQUIRED_ROOT_FILES:
        if required not in names:
            errors.append(f"missing root file: {required}")
    return errors


def check_publish(directory, rid):
    if rid not in NATIVE_FILES:
        return [f"unsupported runtime identifier: {rid}"]
    errors = []
    found = []
    for current, _, files in os.walk(directory):
        for name in files:
            if name.startswith("libvips"):
                found.append(os.path.relpath(os.path.join(current, name), directory))
    expected = NATIVE_FILES[rid]
    if sorted(os.path.basename(f) for f in found) != [expected]:
        errors.append(f"{rid}: expected only {expected} in {directory}, found {sorted(found)}")
    for relative in found:
        with open(os.path.join(directory, relative), "rb") as handle:
            problem = shared_library_problem(relative, handle.read(4096))
        if problem:
            errors.append(f"{relative}: {problem}")
    return errors


def main(argv):
    if len(argv) == 3 and argv[1] == "nupkg":
        errors = check_nupkg(argv[2])
        label = argv[2]
    elif len(argv) == 4 and argv[1] == "publish":
        errors = check_publish(argv[2], argv[3])
        label = f"{argv[2]} ({argv[3]})"
    else:
        print(__doc__)
        return 2
    if errors:
        print(f"FAILED {label}")
        for error in errors:
            print(f"  - {error}")
        return 1
    print(f"OK {label}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
