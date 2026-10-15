# Third-party notices

HagiCode.Libs.Imaging is released under the MIT license. The package bundles the native libraries listed below. They are
separate shared libraries (`.dll`, `.so`, `.dylib`) under `runtimes/<rid>/native` and are loaded at run time. They are
not compiled into the MIT assembly. The bundled files are the unmodified builds from the NetVips.Native 8.18.7 packages.

## Managed dependency

| Component | Version | License |
| --- | --- | --- |
| NetVips (managed binding, installed as a dependency) | 3.2.0 | MIT |

## Bundled native components

Identical component versions and notices were checked for all four runtime identifiers (win-x64, linux-x64, osx-x64,
osx-arm64). Versions come from the `versions.json` shipped in NetVips.Native 8.18.7.

| Component | Version | License |
| --- | --- | --- |
| aom | 3.15.1 | BSD 2-Clause + Alliance for Open Media Patent License 1.0 |
| cairo | 1.18.6 | Mozilla Public License 1.1 |
| cgif | 0.5.4 | MIT License |
| expat | 2.8.5 | MIT License |
| fontconfig | 2.18.3 | fontconfig License (BSD-like) |
| freetype | 2.14.3 | freetype License (BSD-like) |
| fribidi | 1.0.17 | LGPLv3 |
| glib | 2.90.0 | LGPLv3 |
| harfbuzz | 14.5.0 | MIT License |
| highway | 1.4.0 | BSD 3-Clause |
| lcms | 2.19.1 | MIT License |
| libarchive | 3.8.9 | BSD 2-Clause |
| libexif | 0.6.26 | LGPLv3 |
| libffi | 3.8.0 | MIT License |
| libheif | 1.23.5 | LGPLv3 |
| libimagequant | 2.4.1 | BSD 2-Clause |
| libnsgif | not recorded | MIT License |
| libpng | 1.6.58 | libpng License |
| librsvg | 2.63.2 | LGPLv3 |
| libtiff | 4.7.2 | libtiff License (BSD-like) |
| libultrahdr | 2.0.2 | MIT License |
| libvips | 8.18.7 | LGPLv3 |
| libwebp | 1.6.0 | New BSD License |
| libxml2 | 2.15.4 | MIT License |
| mozjpeg | 0826579 | zlib License, IJG License, BSD 3-Clause |
| pango | 1.58.2 | LGPLv3 |
| pixman | 0.46.4 | MIT License |
| proxy-libintl | 0.5 | LGPLv3 |
| zlib-ng | 2.3.3 | zlib License |

Some components are used under the "any later version" clause of the LGPL. aom is used under the BSD 2-Clause license
together with the Alliance for Open Media Patent License 1.0.

## Where the binaries come from

The shared libraries come from the NetVips.Native 8.18.7 NuGet packages, which are built from the libvips-packaging
project (https://github.com/kleisauke/libvips-packaging). HagiCode does not modify these libraries. A consumer who needs
to replace one of them, for example an LGPL component, can do so by providing a compatible shared library with the same
name in the application output.

## Notice generation

This file was generated for gate G1 (2026-10-14) from the component list recorded in the NetVips.Native package. Regenerate
it when the NetVips.Native version changes.
