# Third-party components

## FFmpeg

The recording engine is a separate, unmodified executable copied from the user's existing `D:\ffmpeg\ffmpeg.exe` installation for this personal portable build.

Version: `N-122520-gb637624046-20260121`, built with GCC 15.2.0. Its build configuration enables GPL and version 3 components. The executable is not covered by Flashback's MIT license. Run `tools\ffmpeg.exe -version` and `tools\ffmpeg.exe -L` for its complete build and license information. `licenses/FFmpeg-GPL-3.0.txt` includes the GPL license text.

- Project: https://ffmpeg.org/
- Upstream source revision: https://github.com/FFmpeg/FFmpeg/tree/b637624046
- Licensing information: https://ffmpeg.org/legal.html
- Capture/scaling documentation: https://ffmpeg.org/ffmpeg-filters.html
- Segment and concat documentation: https://ffmpeg.org/ffmpeg-formats.html

This package is prepared for the requesting user's local use. If publishing a redistributed FFmpeg binary, obtain and supply the complete corresponding source, dependency sources and build scripts for that exact build as required by its license. The upstream revision link alone is not a replacement for corresponding source.

## NAudio 2.2.1

Uses NAudio.Core and NAudio.Wasapi, licensed under MIT. Copyright Mark Heath and contributors. Complete license: `licenses/NAudio-MIT.txt`.

- https://github.com/naudio/NAudio
- https://github.com/naudio/NAudio/blob/main/Docs/WasapiLoopbackCapture.md

## .NET 8 Windows runtime

The portable distribution includes the .NET 8.0.31 runtime and Windows Desktop runtime. Their license and third-party notice files are retained in the `licenses` directory.

- https://github.com/dotnet/runtime
- https://github.com/dotnet/wpf

The NVIDIA driver and NVENC implementation are provided by the user's installed NVIDIA software and are not included in this package.

## Vortice and SharpGen

Vortice.Direct3D11, Vortice.DXGI and Vortice.DirectX 3.8.3; Vortice.Mathematics 2.1.0; SharpGen.Runtime and SharpGen.Runtime.COM 2.4.2-beta supply the native Direct3D bindings. Their MIT notices are included in licenses/Vortice-Windows-MIT.txt, licenses/Vortice-Mathematics-MIT.txt and licenses/SharpGen-MIT.txt. Transitive Microsoft System.Text.Json, System.Text.Encodings.Web and System.IO.Pipelines 9.0.1 retain their MIT license notices in the licenses directory.

## Design and protocol references

The trimming workflow was informed by LosslessCut (https://github.com/mifi/lossless-cut); no LosslessCut code is incorporated. OBS Studio's desktop duplication design was reviewed (https://github.com/obsproject/obs-studio), without incorporating its source. Native capture is implemented using Vortice and Windows Direct3D APIs. The optional RTSS notification bridge implements the published RTSSSharedMemoryV2 protocol; RTSS itself is not included or installed by Flashback.
