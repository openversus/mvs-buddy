#!/bin/bash
# Builds RmlUiFlat's native library (rmlui_c with its desktop window) for MVS Buddy, inside the submodule:
#   scripts/build-native.sh linux     -> external/RmlUiFlat/native/build/linux-sniper/librmlui_c.so
#   scripts/build-native.sh windows   -> external/RmlUiFlat/native/build/win-desktop/rmlui_c.dll
# The app bundles whichever its publish target needs (src/MvsBuddy.App/MvsBuddy.App.csproj).
#
# Linux builds in the Steam Runtime "sniper" SDK container (podman or docker), so the library needs glibc 2.29 at most
# and runs on a Steam Deck and on any ordinary desktop distribution; built on a new distribution directly, it would need
# that distribution's glibc. GCC 14 from the image (its default GCC lacks std::bit_cast), FreeType built static first,
# libstdc++ and libgcc linked in.
# Windows cross-builds with RmlUiFlat's own script (clang-cl and an xwin sysroot; external/RmlUiFlat/docs/testing.md).
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
FLAT=$ROOT/external/RmlUiFlat
if [ ! -f "$FLAT/native/CMakeLists.txt" ] || [ ! -f "$FLAT/external/freetype/CMakeLists.txt" ]; then
    echo "RmlUiFlat or its submodules are missing: git submodule update --init --recursive" >&2
    exit 1
fi

case "${1:-}" in
linux)
    ENGINE=$(command -v podman || command -v docker || true)
    if [ -z "$ENGINE" ]; then
        echo "Building for Linux needs podman or docker." >&2
        exit 1
    fi
    IMAGE=registry.gitlab.steamos.cloud/steamrt/sniper/sdk:latest
    # label=disable rather than :Z, which would relabel the files of the checkout itself.
    "$ENGINE" run --rm --security-opt label=disable -v "$FLAT:/src" -w /src "$IMAGE" bash -euc '
        export CC=gcc-14 CXX=g++-14
        B=native/build/linux-sniper
        FT=$B/freetype; FTP=$PWD/$B/freetype-install
        mkdir -p $B
        cmake -S external/freetype -B $FT -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX=$FTP \
            -DBUILD_SHARED_LIBS=OFF -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DFT_DISABLE_ZLIB=ON -DFT_DISABLE_BZIP2=ON \
            -DFT_DISABLE_PNG=ON -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BROTLI=ON > $B.ft.log 2>&1
        cmake --build $FT --target install >> $B.ft.log 2>&1
        FTLIB=$(ls $FTP/lib*/libfreetype.a)
        cmake -S native -B $B -G Ninja -DCMAKE_BUILD_TYPE=Release -DRMLC_SHARED=ON -DRMLC_DESKTOP=ON \
            -DFREETYPE_LIBRARY=$FTLIB -DFREETYPE_INCLUDE_DIR_ft2build=$FTP/include/freetype2 \
            -DFREETYPE_INCLUDE_DIR_freetype2=$FTP/include/freetype2 \
            "-DCMAKE_SHARED_LINKER_FLAGS=-static-libstdc++ -static-libgcc" > $B.log 2>&1
        cmake --build $B --target rmlui_c >> $B.log 2>&1
        echo "built $B/librmlui_c.so (logs: $B.log, $B.ft.log)"'
    # The newest glibc symbol it needs: 2.29 or older runs on a Steam Deck.
    objdump -T "$FLAT/native/build/linux-sniper/librmlui_c.so" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1
    ;;
windows)
    "$FLAT/cross/build-windows.sh" desktop
    ;;
*)
    echo "usage: $0 linux|windows" >&2
    exit 2
    ;;
esac
