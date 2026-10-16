# HagiCode.Libs.Imaging

In-process image work for .NET hosts, using native libvips libraries bundled in the package:

- AVIF conversion (`IAvifConverter`): PNG, JPEG, WebP and GIF (first frame) sources to still AVIF images.
- Model image preparation (`IModelImagePreparer`): turns an uploaded image into one a model accepts (PNG, JPEG, GIF or
  WEBP within 5 MiB and 8000 pixels per side), in memory. See [Model image preparation](#model-image-preparation).

- No `ffmpeg`, `avifenc` or other command-line tool is needed, or looked up on `PATH`.
- The package never starts a child process.
- The native libraries load from the package. No environment variable or library path setting is needed.

## Install

```bash
dotnet add package HagiCode.Libs.Imaging
```

## Usage

```csharp
using HagiCode.Libs.Imaging;

// Dependency injection (singleton converter, limits applied from the configure delegate)
services.AddHagiCodeImaging(options =>
{
    options.MaxInputBytes = 64 * 1024 * 1024;
    options.MaxConcurrentConversions = 4;
});

// Buffer to buffer: AVIF bytes are in result.Data
var result = await converter.ConvertAsync(uploadBytes, new AvifConversionOptions
{
    Resize = new AvifResize(AvifResizeMode.Fit, 200, 200),
    Alpha = AvifAlphaMode.Flatten,
    FlattenColor = "#64748b",
});

// Stream to stream: nothing is written to the destination unless the conversion succeeds
await converter.ConvertAsync(sourceStream, destinationStream, options, cancellationToken);

// File to file: written to a temporary file, then moved into place
await converter.ConvertFileAsync("in.png", "out.avif", options, overwrite: true, cancellationToken);
```

Check the runtime at start-up if you need a health signal:

```csharp
var status = AvifRuntime.Probe(); // cached; never throws
if (!status.IsAvailable) { /* log status.UnavailableReason and fall back */ }
```

## Model image preparation

`IModelImagePreparer` prepares an image for a model that accepts only PNG, JPEG, GIF or WEBP within a size and pixel
limit. The stored file is never changed, and nothing is written to disk.

```csharp
services.AddHagiCodeImaging(); // registers IAvifConverter and IModelImagePreparer as singletons

var prepared = await preparer.PrepareAsync(path, ModelImageLimits.Default, cancellationToken);
if (prepared.IsUsable)
{
    // prepared.MediaType and prepared.Bytes (ReadOnlyMemory<byte>) go to the model.
}
else
{
    // prepared.Reason says why; the image is skipped. Image bytes are never in Reason or ToString.
}
```

Format is decided from the content, never from the file name. Each image gets one outcome:

| Outcome | What happened |
| --- | --- |
| `PassedThrough` | A PNG, JPEG, GIF or WEBP within the limits. The bytes are returned unchanged. A GIF keeps its frames. |
| `Rasterized` | An SVG was rendered to PNG from its buffer. Its longest edge is at most 2048 pixels. |
| `Reduced` | The image was downscaled and re-encoded. JPEG for opaque images (quality 85, 75, 65, then 55), PNG for images with alpha. An animated GIF becomes its first frame. EXIF orientation is applied, so the result is upright. |
| `NotPreparable` | The image cannot be sent. Bytes are empty and `Reason` gives a short phrase. |

`ModelImageLimits` (per call, defaults are the Claude limits):

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxBytes` | 5 MiB | Largest encoded size of a prepared image. |
| `MaxDimension` | 8000 | Largest width and height of a prepared image, in pixels. |
| `MaxDecodePixels` | 64,000,000 | Largest width times height that may be decoded. Checked from the header first, so an oversized declared size is refused before any pixel is decoded. |
| `MaxSourceBytes` | 64 MiB | Largest source file read into memory. |

Reduction makes at most 6 encode attempts. If none fits, the result is `NotPreparable`. The preparer never throws for bad
input: a corrupt, truncated, unsupported or malformed image is a `NotPreparable` result. Only cancellation is thrown, as
`OperationCanceledException`.

SVG handling is bounded. The document is loaded from its buffer with no base path, so relative references cannot
resolve. Entity declarations (`<!ENTITY`) are refused before rendering. A declared size is never rendered at full size:
the render scale keeps the longest edge within 2048 pixels.

### SVG and untrusted-operation blocking

Probe result (task 1.4a, libvips bundled in NetVips.Native 8.18.7, Linux x64):

- With `NetVips.BlockUntrusted = true`, libvips refuses the SVG loader for every input (`unable to call svgload_buffer`).
  `AvifConverter` sets that flag for the process by default (`ConfigureProcessWideEngineSettings`). While the flag is on,
  the preparer reports an SVG as `NotPreparable` with reason "could not be rasterized".
- With the flag off, SVG rasterization works. A `file://` reference, an absolute-path reference and an `http://`
  reference are not fetched. A local canary image was not drawn, and no HTTP request reached a local listener.

The blocking flag is process-wide and the converter does not use generic or untrusted loaders, so the flag adds no
protection to AVIF conversion. Choosing between the two is an open decision; see the change design notes.

## Options

`AvifConversionOptions` (per call):

| Option | Default | Meaning |
| --- | --- | --- |
| `Quality` | 60 | 0 (smallest) to 100 (largest, closest to the source). |
| `Speed` | 6 | 0 (slowest) to 10 (fastest). Does not change validity. |
| `ChromaSubsampling` | `Auto` | `Auto`, `Yuv420` or `Yuv444`. |
| `Alpha` | `Preserve` | `Preserve` keeps transparency. `Flatten` composites onto `FlattenColor`. |
| `FlattenColor` | `#ffffff` | `#rrggbb`. Used only with `Flatten`. |
| `Resize` | none | `AvifResize(Fit or Cover, width, height)`. `Fit` never enlarges. `Cover` fills the box and crops from the center. |
| `StripMetadata` | `true` | Removes EXIF, GPS and other metadata. EXIF orientation is always applied to the pixels first. |

`AvifConverterOptions` (per converter):

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxInputBytes` | 64 MiB | Largest source. Checked before and while reading. |
| `MaxPixels` | 64,000,000 | Largest width times height after orientation. Checked from the header, before decoding. |
| `MaxConcurrentConversions` | `max(1, ProcessorCount / 2)` | Encodes running at the same time. Extra calls wait. |
| `ConfigureProcessWideEngineSettings` | `true` | Sets libvips' operation cache to zero and caps its thread count once per process. Set `false` if the host manages libvips itself. |

## Result

`AvifConversionResult` reports `SourceFormat`, `SourceWidth` and `SourceHeight` (after orientation, before resize),
`OutputWidth`, `OutputHeight`, `OutputByteLength`, `HasAlpha`, `Elapsed` and `Data` (filled only by the buffer overload).

## Errors

Every failure is an `AvifConversionException` with a stable `ErrorCode`. Branch on the code, not the message.

| Code | When |
| --- | --- |
| `InvalidOptions` | A value is out of range, `FlattenColor` is not `#rrggbb`, a resize target is not positive, or the source and destination are the same file. |
| `UnsupportedSourceFormat` | The bytes are not PNG, JPEG, WebP, GIF or AVIF. The file name is not used. |
| `CorruptSource` | The source cannot be decoded, for example a truncated file. |
| `SourceTooLarge` | The source is above `MaxInputBytes`. |
| `DimensionsTooLarge` | The declared pixels are above `MaxPixels`. Decoding has not started. |
| `DestinationExists` | The destination exists and `overwrite` is false. |
| `NativeRuntimeUnavailable` | The platform is not supported, or a native library cannot load. The message names the detected runtime identifier and the supported ones. |
| `EncodeFailed` | The AVIF encoder did not produce output. |

Cancellation ends as `OperationCanceledException`, not as an `AvifConversionException`, and leaves no output behind.

## Supported platforms

| Runtime identifier | Native file in the package |
| --- | --- |
| `win-x64` | `runtimes/win-x64/native/libvips-42.dll` |
| `linux-x64` (glibc) | `runtimes/linux-x64/native/libvips.so.42` |
| `osx-x64` | `runtimes/osx-x64/native/libvips.42.dylib` |
| `osx-arm64` | `runtimes/osx-arm64/native/libvips.42.dylib` |

The Linux build needs the standard C++ runtime (`libstdc++`) that glibc distributions provide. Other platforms, including
`linux-musl-x64`, `linux-arm64` and `win-arm64`, report `NativeRuntimeUnavailable`. Signature detection (`AvifFormat.IsAvif`)
keeps working on every platform.

Model image preparation on an unsupported platform, or when `AvifRuntime.Probe()` is unavailable: an image within the
limits still passes through unchanged (its size and pixels are read from the header in managed code, and no decode is
run). Anything that needs conversion (over a limit, SVG, or a dimension above the limit) is `NotPreparable` with a reason
that names the runtime.

## Limits and behavior

- Input and pixel limits protect memory. The pixel check reads the header and does not decode the pixels.
- Loaders are explicit and use `failOn: Error`, so a truncated file is rejected instead of decoded partially.
- Concurrent encodes are bounded by `MaxConcurrentConversions`.
- Animated GIF input yields a single still image built from the first frame. Animated AVIF output is not supported.
- A running native encode cannot be interrupted. Cancellation takes effect when the encode returns, and the output is discarded.

## Package contents and size

The package carries the four native libraries (about 18 to 20 MB each). A publish for one runtime identifier copies only
that platform's library. The nupkg is about 34 MB. It contains `README.md`, `icon.png` and `THIRD-PARTY-NOTICES.md`.

## Third-party notices

The bundled native components (libvips, libheif, aom and others) are listed with versions and licenses in
`THIRD-PARTY-NOTICES.md`, packed at the root of this package.

## Integration

The backend integration guide, with option mappings from the current ffmpeg-based AVIF path and the thumbnail mapping, is
in the monorepo at `docs/avif-backend-integration-guide.md`.
