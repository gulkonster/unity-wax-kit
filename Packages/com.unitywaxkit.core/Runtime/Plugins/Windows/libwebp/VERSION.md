# libwebp for Unity WAX Kit (Windows x64)

Decodes WebP NFT images on Windows (still and animated). Android and WebGL use the platform's own decoder instead.

| | |
|---|---|
| Version | libwebp **1.6.0** (decoder 1.6.0, demux 1.6.0) |
| Source | Official release archive `https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.6.0.tar.gz` |
| Source SHA-256 | `e4ab7009bf0629fd11982d4c2aa83964cf244cffba7347ecd39019a9e38c4564` |
| Signature | `libwebp-1.6.0.tar.gz.asc` checked: good signature from "WebP release signing key <webp-discuss@webmproject.org>", key `6B0E6B70976DE303EDF2F601F9C3D6BDB8232B5D` |
| Built | 2026-11-04, Windows 11 x64 |
| Compiler | MSVC 14.50.35717 (cl.exe 14.50.35730.0), Visual Studio Community 2026 18.5.2 |
| Generator | CMake 4.2.3-msvc3, Ninja 1.12.1 (both bundled with Visual Studio) |
| Flags | `/DWIN32 /D_WINDOWS /O2 /Ob2 /DNDEBUG`, static C runtime (`/MT`), linker `/machine:x64 /INCREMENTAL:NO` |
| Imports | `libwebp.dll`: libsharpyuv.dll, KERNEL32.dll · `libwebpdemux.dll`: libwebp.dll, KERNEL32.dll · `libsharpyuv.dll`: KERNEL32.dll (`dumpbin /dependents`; no Visual C++ runtime needed) |
| License | BSD-3-Clause (`LICENSE.txt`) and Google's patent grant (`PATENTS.txt`) |

## Shipped files (SHA-256)

| File | Bytes | SHA-256 |
|---|---|---|
| `x86_64/libwebp.dll` | 494080 | `9A585DB747C6A1CA820123FFACC43578E8BF8EAD84CB96BF7F33C88AB7F3BBE9` |
| `x86_64/libwebpdemux.dll` | 111104 | `6FB2E97E02F3EEFB830039A343AE6085266E9FDCE991431F6521A625E496D06B` |
| `x86_64/libsharpyuv.dll` | 138240 | `9608F3B8903A122AA6EC60FEAA2E8D8F4B1AC2E69FD0670804D4E27EE63F4310` |

The hashes identify the shipped files; they are a record, not a security guarantee. A rebuild with the same command gives equivalent DLLs but not necessarily the same SHA-256 (compiler build, timestamps).

`libsharpyuv.dll` is shipped because libwebp 1.6.0's CMake links it into `libwebp.dll` (used by the encoder half); the kit only decodes.

## Exact build

In "x64 Native Tools Command Prompt" (`vcvars64.bat`), from the folder that holds the extracted `libwebp-1.6.0`:

```bat
cmake -S libwebp-1.6.0 -B build -G Ninja ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DBUILD_SHARED_LIBS=ON ^
  -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded ^
  -DWEBP_ENABLE_SIMD=ON ^
  -DWEBP_USE_THREAD=ON ^
  -DWEBP_NEAR_LOSSLESS=ON ^
  -DWEBP_ENABLE_SWAP_16BIT_CSP=OFF ^
  -DWEBP_ENABLE_WUNUSED_RESULT=OFF ^
  -DWEBP_BUILD_ANIM_UTILS=OFF ^
  -DWEBP_BUILD_CWEBP=OFF ^
  -DWEBP_BUILD_DWEBP=OFF ^
  -DWEBP_BUILD_GIF2WEBP=OFF ^
  -DWEBP_BUILD_IMG2WEBP=OFF ^
  -DWEBP_BUILD_VWEBP=OFF ^
  -DWEBP_BUILD_WEBPINFO=OFF ^
  -DWEBP_BUILD_LIBWEBPMUX=OFF ^
  -DWEBP_BUILD_WEBPMUX=OFF ^
  -DWEBP_BUILD_EXTRAS=OFF ^
  -DWEBP_BUILD_WEBP_JS=OFF ^
  -DWEBP_BUILD_FUZZTEST=OFF
cmake --build build --target webpdemux
```

Every option of libwebp 1.6.0's `CMakeLists.txt` is set explicitly above (`WEBP_LINK_STATIC` only affects the tools, which are off). Output: `build\libwebp.dll`, `build\libwebpdemux.dll`, `build\libsharpyuv.dll`.

## Updates

libwebp had CVE-2023-4863 (heap overflow, exploited in the wild). Watch the libwebp releases (`https://github.com/webmproject/libwebp/releases`) and NVD for "libwebp". A security fix means: new source archive and signature check, rebuild with the command above, new hashes here and in `THIRD_PARTY_NOTICES.md`, and a kit patch release naming the libwebp version in the CHANGELOG.
