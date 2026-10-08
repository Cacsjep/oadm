# Third-party notices

OADM is licensed under Apache-2.0 (see `LICENSE`). The published client additionally contains the
components below. This file lists only components with notice or source obligations beyond the
usual NuGet package licenses (MIT/Apache/BSD), which are listed in each package.

## FFmpeg (live view video decoder, client only)

- What: FFmpeg 9.0.2 shared libraries `avcodec`, `avutil`, `swscale`, `swresample`
  (`avformat`, `avfilter`, `avdevice` and the `ffmpeg`/`ffprobe` executables of the package are
  not shipped; see `OadmTrimFfmpegNatives` in `src/Oadm.Client/Oadm.Client.csproj`).
- Package: `DevEnvy.FFmpeg.Binaries.LGPLv2.Runtime.<rid>` 9.0.2 (win-x64, linux-x64, osx-arm64,
  osx-x64 and more), https://github.com/devenvy/ffmpeg-dotnet
- License: **LGPL-2.1-or-later**. The build is configured without `--enable-gpl`,
  `--enable-version3` and `--enable-nonfree` (no x264/x265, no GPL or nonfree parts, no
  Apache-2.0 dependencies linked into an LGPLv2 build). Bundled third-party libraries inside the
  FFmpeg binaries (dav1d, openh264, libvpx, zlib, ...) are under BSD/MIT-style licenses; their
  texts ship in the package under `legal/licenses/`.
- License texts: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html and the package's
  `legal/` folder (`COPYING.LGPLv2.1`, `LICENSE.md`, `LICENSE-NOTICE.txt`).
- Corresponding source: the unmodified FFmpeg release https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz
  plus the public, reproducible build recipe https://github.com/devenvy/ffmpeg
  (ref `fcd83b13adbbbdf616f3a48a565f1913dbdfa403`), which pins every bundled library.
- The IJG notice required by FFmpeg's libjpeg-derived files: "This software is based in part on
  the work of the Independent JPEG Group."
- How it is linked: dynamically. The libraries are embedded in the single-file client exe
  (`IncludeNativeLibrariesForSelfExtract`) and extracted by the .NET host at startup to
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR` (default: the user's temp folder, `.net/Oadm.Client/<hash>`).
  To use your own build of the libraries (LGPL section 6), set `OADM_FFMPEG_DIR` to a folder
  containing ABI-compatible `avcodec`, `avutil`, `swscale` and `swresample` (FFmpeg 9.x); the client
  loads them from there instead. `Oadm.Client --check-decoder` shows which libraries were loaded.

## FFmpeg.AutoGen (bindings, client only)

- Package `FFmpeg.AutoGen` 9.0.1.1, https://github.com/Ruslan-B/FFmpeg.AutoGen, MIT License,
  Copyright (c) 2025 Ruslan Balanukhin (Rationale One).

## PDFsharp and MigraDoc (Snapshot report plugin, server only)

- Package `PDFsharp-MigraDoc` 6.2.4 (PDFsharp, MigraDoc and their helper assemblies, the
  cross-platform "Core" build), https://github.com/empira/PDFsharp, MIT License,
  Copyright (c) 2005-2025 empira Software GmbH, Troisdorf (Cologne Area), Germany.
- Shipped in `plugins/oadm.snapshot-report/` next to `Oadm.Plugins.SnapshotReport.Server.dll`; it
  builds the maintenance report PDF. 

## Roboto font (Snapshot report plugin, embedded)

- `Roboto-Regular.ttf` and `Roboto-Bold.ttf` (Google Roboto 2.x, as shipped in the
  MaterialDesignThemes package) are embedded in `Oadm.Plugins.SnapshotReport.Server.dll` so the PDF
  report renders identically on every OS. Apache License 2.0, Copyright 2011 Google Inc.,
  https://github.com/googlefonts/roboto. The PDF embeds subsets of these fonts.
