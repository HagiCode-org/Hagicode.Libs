# AVIF backend integration guide

This guide describes how `repos/hagicode-core` can move its AVIF output from the ffmpeg process to `HagiCode.Libs.Imaging`, and how session background thumbnails can move from an ImageSharp JPEG to AVIF. It is the reference for a separate adoption change. It does not change the backend.

Backend paths are relative to `repos/hagicode-core`. Library paths are relative to `repos/Hagicode.Libs`. Line numbers were read from the code on 2026-10-14.

The library converts in-process. It never starts a process, never looks up a tool on `PATH`, and never calls `ffmpeg`, `avifenc` or any other executable. Adopting it removes the ffmpeg dependency from the AVIF path. Nothing in this guide asks the reader to install or set up an external tool for AVIF.

## 1. Summary

- **Hero avatars.** Replace `HeroAvatarAvifEncoder.RenderAsync` with `IAvifConverter.ConvertFileAsync`. The calibrated `Quality` is **50** (section 9.2). Speed 6, `Cover`, flatten onto `#64748b` and `Yuv420` match the ffmpeg path. Pooled over the sample set, the library output at 64 and 128 px is about 23% smaller, at the same or higher PSNR (+1.44 and +0.02 dB). At 512 px it is 18% smaller and 2.2 dB lower on average. Several samples are further below at 512 px, so the average hides real losses (section 9.2).
- **Session background thumbnails.** Replace the ImageSharp JPEG encode with `Fit` 200x200 at calibrated `Quality` **70** (section 9.3). Qualities 40, 50 and 60 do not reach the JPEG PSNR. At 70 the AVIF thumbnail is 33% smaller and 0.53 dB higher on average. Keep the JPEG branch as the fallback when `AvifRuntime.Probe().IsAvailable` is false.
- **Removable after adoption.** The ffmpeg process call, the `libaom-av1` and `avif` capability probe and the ffmpeg lookup for AVIF (section 7). The WebP re-encoders in the compression matrix also use ffmpeg, and they stay (section 8).

## 2. Current backend behavior

### 2.1 Hero avatar encoder

| Fact | Value | Source |
| --- | --- | --- |
| Encoder | ffmpeg with `libaom-av1`, still picture flag | `src/PCode.Application/HeroAvatarAvifEncoder.cs:77-80` |
| Quality | `-crf 28` with `-b:v 0` | `HeroAvatarAvifEncoder.cs:81-84`; `HeroAvatarFormat.EncodeCrf = 28` at `src/PCode.Application/HeroAvatarFormat.cs:25` |
| Speed | `-cpu-used 6` | `HeroAvatarAvifEncoder.cs:85-86` |
| Frames and audio | `-frames:v 1`, `-an` | `HeroAvatarAvifEncoder.cs:76`, `:87-88` |
| Sizes | 64, 128 and 512, one file each | `HeroAvatarFormat.cs:20` (`Sizes = [64, 128, 512]`); loop at `src/PCode.Application/HeroCustomAvatarStorageService.cs:54` |
| Crop | cover: `scale=N:N:force_original_aspect_ratio=increase`, then `crop=N:N` (centre) | `HeroAvatarAvifEncoder.cs:119-121` (`BuildFilterGraph`, line 116) |
| Flatten | `color=c=0x64748b`, overlaid under the source | `HeroAvatarFormat.FlattenBackgroundColor = "0x64748b"` at `HeroAvatarFormat.cs:33`; `HeroAvatarAvifEncoder.cs:122-123` |
| Chroma | `format=yuv420p` | `HeroAvatarAvifEncoder.cs:123` |
| Full filter graph | `[0:v]scale=N:N:force_original_aspect_ratio=increase,crop=N:N,format=rgba[fg];color=c=0x64748b:s=NxN,format=rgba[bg];[bg][fg]overlay=shortest=1:format=auto,format=yuv420p[out]` | `HeroAvatarAvifEncoder.cs:116-124` |
| Command shape | `-hide_banner -loglevel error -y -i SRC -filter_complex GRAPH -map [out] -an -c:v libaom-av1 -still-picture 1 -crf 28 -b:v 0 -cpu-used 6 -frames:v 1 OUT.avif` | `HeroAvatarAvifEncoder.cs:66-89` |
| Metadata and orientation | not set in the arguments; ffmpeg's defaults apply and were not measured | `HeroAvatarAvifEncoder.cs:66-89` |

### 2.2 Session background thumbnails

| Fact | Value | Source |
| --- | --- | --- |
| Load | `Image.LoadAsync(finalPhysicalPath)`, on the file after compression | `src/PCode.Application/Services/SessionBackgroundImageAppService.cs:63`, `:68` |
| Width and height stored | `image.Width` and `image.Height` as loaded | `SessionBackgroundImageAppService.cs:69-70`, `:85-97` |
| Resize | `ResizeMode.Max`, `Size(200, 200)` | `SessionBackgroundImageAppService.cs:72-76` |
| Encode | `SaveAsJpegAsync` with `JpegEncoder { Quality = 85 }` | `SessionBackgroundImageAppService.cs:78-81` |
| File name | `{imageId}.thumb.jpg` | `src/PCode.DomainServices/SessionBackgroundImage/SessionBackgroundImageDomainService.cs:55` |
| Stored path | `ThumbnailPath = paths.ThumbnailRelativePath` | `SessionBackgroundImageAppService.cs:95`; `src/PCode.DomainServices/SessionBackgroundImage/SessionBackgroundImageMetadata.cs:19` |
| Served content type | `"image/jpeg"` for every thumbnail | `SessionBackgroundImageAppService.cs:153-154` |
| Cleanup on failure | `CleanupImageFiles` in the `catch` block | `SessionBackgroundImageAppService.cs:102-106` |

## 3. Hero avatar mapping

### 3.1 Option mapping

| Backend today | `AvifConversionOptions` | Library symbol |
| --- | --- | --- |
| `-crf 28` (`HeroAvatarFormat.EncodeCrf`, `HeroAvatarFormat.cs:25`) | `Quality = 50` (calibrated, section 9.2) | `AvifConversionOptions.Quality`. The library default is `AvifConversionOptions.DefaultQuality` (60, `AvifConversionOptions.cs:49`). |
| `-cpu-used 6` (`HeroAvatarAvifEncoder.cs:85-86`) | `Speed = 6` | `AvifConversionOptions.DefaultSpeed` (`AvifConversionOptions.cs:52`). The engine passes `effort = 9 - Speed` to libheif (`LibvipsAvifEngine.cs:47`). The equivalence to libaom `cpu-used` was not verified. |
| Scale and centre crop (`HeroAvatarAvifEncoder.cs:119-121`) | `Resize = new AvifResize(AvifResizeMode.Cover, size, size)` | `AvifResizeMode.Cover`; the engine crops from the centre (`LibvipsAvifEngine.cs:141`). |
| Flatten onto `#64748b` (`HeroAvatarFormat.cs:33`) | `Alpha = AvifAlphaMode.Flatten`, `FlattenColor = "#64748b"` | `AvifAlphaMode.Flatten` (`LibvipsAvifEngine.cs:32`) |
| `format=yuv420p` (`HeroAvatarAvifEncoder.cs:123`) | `ChromaSubsampling = AvifChromaSubsampling.Yuv420` | `AvifChromaSubsampling.Yuv420` |
| Still picture (`HeroAvatarAvifEncoder.cs:79-80`) | no option: each call writes one still image | none |
| Sizes 64, 128, 512 (`HeroAvatarFormat.cs:20`) | one `ConvertFileAsync` call per size | none |
| Metadata (not set, see 2.1) | `StripMetadata = true` (default) | `AvifConversionOptions.StripMetadata` (`LibvipsAvifEngine.cs:49`) |
| Orientation (ffmpeg default, not verified) | always applied to the pixels before resize | `Engine/LibvipsAvifEngine.cs:24` (`Autorot`) |

### 3.2 Sample: options

```csharp
using HagiCode.Libs.Imaging;

namespace PCode.Application.Adoption;

// Replaces the ffmpeg arguments built in HeroAvatarAvifEncoder.RenderAsync (HeroAvatarAvifEncoder.cs:66-89).
public static class HeroAvatarAvifMapping
{
    // Calibrated in guide section 9.2: the lowest Quality that meets the size and PSNR rules over the hero sample set.
    public const int CalibratedQuality = 50;

    public static AvifConversionOptions ForSize(int size) => new()
    {
        Quality = CalibratedQuality,                                 // was -crf 28 (HeroAvatarFormat.cs:25)
        Speed = AvifConversionOptions.DefaultSpeed,                  // was -cpu-used 6 (HeroAvatarAvifEncoder.cs:85-86)
        ChromaSubsampling = AvifChromaSubsampling.Yuv420,            // was format=yuv420p (HeroAvatarAvifEncoder.cs:123)
        Alpha = AvifAlphaMode.Flatten,                               // was overlay onto a colour (HeroAvatarAvifEncoder.cs:122)
        FlattenColor = "#64748b",                                    // HeroAvatarFormat.FlattenBackgroundColor (HeroAvatarFormat.cs:33)
        Resize = new AvifResize(AvifResizeMode.Cover, size, size),   // was scale and crop (HeroAvatarAvifEncoder.cs:119-121)
    };
}
```

### 3.3 Sample: render through the converter

```csharp
using HagiCode.Libs.Imaging;

namespace PCode.Application.Adoption;

// Replaces HeroAvatarAvifEncoder.RenderAsync (HeroAvatarAvifEncoder.cs:56-114), its capability probe
// (EnsureCapabilityAsync, HeroAvatarAvifEncoder.cs:42-50) and the ffmpeg lookup (ResolveFfmpegPath, HeroAvatarAvifEncoder.cs:24-36).
public sealed class HeroAvatarAvifRenderer(IAvifConverter converter)
{
    public async Task RenderAsync(string sourcePath, string outputPath, int size, CancellationToken cancellationToken)
    {
        try
        {
            // The caller keeps the staging rule: nothing is persisted until every size has encoded
            // (HeroCustomAvatarStorageService.cs:45).
            await converter.ConvertFileAsync(
                sourcePath,
                outputPath,
                HeroAvatarAvifMapping.ForSize(size),
                overwrite: false,
                cancellationToken);
        }
        catch (AvifConversionException exception)
        {
            throw AvifErrorMapping.ToHeroException(exception);
        }
    }
}
```

The renderer replaces the staging loop at `HeroCustomAvatarStorageService.cs:54-58` and the upgrade call at `HeroCustomAvatarLegacyFormatMigrator.cs:74`. The lookup and probe calls at `HeroCustomAvatarStorageService.cs:46-47` and `HeroCustomAvatarLegacyFormatMigrator.cs:64-65` are no longer needed.

## 4. Session background thumbnail mapping

### 4.1 Mapping table

| Backend today | Library and adoption step | Notes |
| --- | --- | --- |
| `Image.LoadAsync` (`SessionBackgroundImageAppService.cs:68`) | `ConvertFileAsync` reads the file. `AvifConversionResult.SourceWidth` and `SourceHeight` replace `image.Width` and `image.Height` (`SessionBackgroundImageAppService.cs:69-70`). | The values differ for EXIF-rotated uploads. ImageSharp reports the stored pixels: a JPEG tagged orientation 6 with 800x600 pixels is reported as 800x600. The library reports 600x800, after orientation. Decide whether the stored `Width` and `Height` (`SessionBackgroundImageAppService.cs:85-97`) keep the stored size. |
| `ResizeMode.Max`, `Size(200, 200)` (`SessionBackgroundImageAppService.cs:72-76`) | `AvifResizeMode.Fit`, 200, 200 | ImageSharp `Max` enlarges: a 100x100 source becomes 200x200. `Fit` never enlarges: 100x100 stays 100x100. Small uploads get smaller thumbnails. |
| `JpegEncoder { Quality = 85 }` (`SessionBackgroundImageAppService.cs:78-81`) | `Quality = 70` (calibrated, section 9.3), `ChromaSubsampling = AvifChromaSubsampling.Yuv420` | The ImageSharp JPEG output is 4:2:0. Checked on the baseline outputs: luma is sampled 2x2. |
| Transparent sources (JPEG output has no alpha channel) | `Alpha = AvifAlphaMode.Flatten`, `FlattenColor = "#64748b"` (proposed) | The calibration sources were opaque, so this colour is a policy choice and was not calibrated. |
| `{imageId}.thumb.jpg` (`SessionBackgroundImageDomainService.cs:55`) | `{imageId}.thumb.avif`. `ResolveStoragePaths` (`SessionBackgroundImageDomainService.cs:47`) must take the thumbnail extension, because it hard-codes `.thumb.jpg` today. | Adoption step. |
| `"image/jpeg"` for every thumbnail (`SessionBackgroundImageAppService.cs:153-154`) | `image/avif` for `.thumb.avif`, `image/jpeg` for `.thumb.jpg` | Derive the type from the stored `ThumbnailPath` extension, and ship it with the file-name change. The web client was not checked for a JPEG assumption. |
| The JPEG encode always runs | `AvifRuntime.Probe().IsAvailable` false: keep the existing ImageSharp branch (`SessionBackgroundImageAppService.cs:68-82`) | Section 4.2 sample. |
| Legacy records whose `ThumbnailPath` ends in `.thumb.jpg` | unchanged. `GetContentAsync` resolves the stored path (`SessionBackgroundImageAppService.cs:139-140`). | Legacy records stay readable without migration. |
| Thumbnail source is the post-compression file (`SessionBackgroundImageAppService.cs:63-68`) | the library reads PNG, JPEG, WebP, GIF and AVIF | The matrix can turn a JPEG upload into WebP first. The calibration used the original upload formats. |

### 4.2 Sample: thumbnail with JPEG fallback

```csharp
using HagiCode.Libs.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace PCode.Application.Adoption;

public sealed record ThumbnailWriteResult(string Path, string ContentType, int SourceWidth, int SourceHeight);

// AVIF thumbnail when the runtime is available; otherwise the existing ImageSharp JPEG branch.
public sealed class SessionThumbnailEncoder(IAvifConverter converter)
{
    // Calibrated in guide section 9.3. Quality 60 is the largest value in the requested grid and misses the JPEG PSNR.
    public const int CalibratedQuality = 70;

    public static readonly AvifConversionOptions ThumbnailOptions = new()
    {
        Quality = CalibratedQuality,
        Speed = AvifConversionOptions.DefaultSpeed,
        ChromaSubsampling = AvifChromaSubsampling.Yuv420,
        Alpha = AvifAlphaMode.Flatten,
        FlattenColor = "#64748b",
        Resize = new AvifResize(AvifResizeMode.Fit, 200, 200),
    };

    public async Task<ThumbnailWriteResult> WriteAsync(
        string sourcePath,
        string avifPath,
        string jpegPath,
        CancellationToken cancellationToken)
    {
        if (AvifRuntime.Probe().IsAvailable)
        {
            var result = await converter.ConvertFileAsync(sourcePath, avifPath, ThumbnailOptions, overwrite: false, cancellationToken);
            return new ThumbnailWriteResult(avifPath, "image/avif", result.SourceWidth, result.SourceHeight);
        }

        // Fallback: the existing branch, unchanged (SessionBackgroundImageAppService.cs:68-82).
        using var image = await Image.LoadAsync(sourcePath, cancellationToken);
        var width = image.Width;
        var height = image.Height;
        using (var thumbnail = image.Clone(ctx => ctx.Resize(new ResizeOptions
               {
                   Mode = ResizeMode.Max,
                   Size = new Size(200, 200),
               })))
        {
            await thumbnail.SaveAsJpegAsync(jpegPath, new JpegEncoder { Quality = 85 }, cancellationToken);
        }

        return new ThumbnailWriteResult(jpegPath, "image/jpeg", width, height);
    }
}
```

### 4.3 Size verdict

At the calibrated Quality of 70, the AVIF thumbnails average 5,769 bytes against 8,609 bytes for the ImageSharp JPEG. That is 0.67x, and the mean PSNR is 0.53 dB higher. Quality 60, the largest point in the requested grid, is 0.50x the size but 0.73 dB below the JPEG. AVIF is smaller at both points. Encode times are not like-for-like (section 9.3).

## 5. Package reference and DI wiring

### 5.1 PackageReference

The backend uses central package management (`Directory.Packages.props:3` sets `ManagePackageVersionsCentrally` to true). The version belongs in `Directory.Packages.props`, and the project file references the package without a version. This is the same pattern as `SixLabors.ImageSharp`, which is versioned at `Directory.Packages.props:50` and referenced at `src/PCode.Application/PCode.Application.csproj:5`.

```xml
<!-- Directory.Packages.props -->
<PackageVersion Include="HagiCode.Libs.Imaging" Version="<released version>" />

<!-- src/PCode.Application/PCode.Application.csproj -->
<PackageReference Include="HagiCode.Libs.Imaging" />
```

The version is the one published with the release that carries the package. The sample project in the verification step uses the same central layout.

The native library is inside the package under `runtimes/<rid>/native`. A consumer that references the packed package gets the library copied to its output, with or without a `RuntimeIdentifier` (verified). A `ProjectReference` to `src/HagiCode.Libs.Imaging` does not carry those files (verified with a RuntimeIdentifier set). A consumer that references the project must also reference the `NetVips.Native.<rid>` packages, as the library's own test project does.

### 5.2 DI wiring

Register the converter next to the existing `services.AddHagiCodeSkills` call (`src/PCode.Application/PCodeApplicationModule.cs:51`).

```csharp
using HagiCode.Libs.Imaging;
using Microsoft.Extensions.DependencyInjection;

namespace PCode.Application.Adoption;

public static class ImagingRegistration
{
    // Called from PCodeApplicationModule's service registration, next to AddHagiCodeSkills.
    public static IServiceCollection AddBackendImaging(this IServiceCollection services)
    {
        return services.AddHagiCodeImaging(options =>
        {
            // The hero upload limit (HeroAvatarFormat.cs:22) and the session upload limit (SessionBackgroundImageAppService.cs:30) are both 10 MiB.
            options.MaxInputBytes = HeroAvatarFormat.MaxUploadBytes;
            options.MaxConcurrentConversions = Math.Max(1, Environment.ProcessorCount / 2);
        });
    }
}
```

- `IAvifConverter` is registered as a singleton, and the `AvifConverterOptions` instance is registered as well.
- `MaxPixels` (default 64,000,000) is checked from the header before any decoding. The backend has no pixel limit today.
- By default the first converter sets libvips' process-wide operation cache to zero, blocks untrusted loaders and caps libvips threads (`AvifRuntime.ConfigureProcessWideSettings`, `src/HagiCode.Libs.Imaging/AvifRuntime.cs:41-49`). The backend does not reference NetVips or libvips anywhere today, so the default is expected. The option that turns this off is `AvifConverterOptions.ConfigureProcessWideEngineSettings`.
- The startup health signal is `AvifRuntime.Probe()`. It is cached and never throws. It encodes a tiny image and decodes five fixtures on the first call.

## 6. Errors

### 6.1 Mapping table

Each `AvifErrorCode` (`src/HagiCode.Libs.Imaging/AvifErrorCode.cs`) maps to the backend message that already exists for the same case. Where no message exists, the row says so.

| `AvifErrorCode` | Meaning in the library | Hero avatar today | Session background today | Proposed user-facing mapping |
| --- | --- | --- | --- | --- |
| `InvalidOptions` | A value is out of range (programming error) | none | none | Log and return a generic failure. Never show it to the user. |
| `UnsupportedSourceFormat` | Bytes are not PNG, JPEG, WebP, GIF or AVIF | `UnsupportedTypeMessage` (`HeroCustomAvatarStorageService.cs:11`, thrown at `:164`) | `SessionBackgroundImageErrorCodes.InvalidFileType` (`src/PCode.DomainServices/SessionBackgroundImage/SessionBackgroundImageDomainService.cs:340`, thrown at `SessionBackgroundImageAppService.cs:173-175`) | Hero: `UserFriendlyException` with the existing message. Session: `BusinessException` with `InvalidFileType`. The hero signature check runs before the encode (`HeroCustomAvatarStorageService.cs:43`). The session check looks only at the extension and content type (`SessionBackgroundImageAppService.cs:167-184`), so a mislabelled file reaches the converter and this code can occur. |
| `CorruptSource` | The type is allowed but the file cannot be decoded | The ffmpeg error text is thrown (`HeroAvatarAvifEncoder.cs:110-112`), which leaks encoder output to the user. Signature errors at `HeroCustomAvatarStorageService.cs:196` and `:214`. | No typed error exists. The ImageSharp exception propagates through the `catch` at `SessionBackgroundImageAppService.cs:102-106`. | Hero: `UserFriendlyException("Hero avatar compression failed.")`, the text at `HeroAvatarAvifEncoder.cs:111`, with no encoder output. Session: keep the propagating path, or add a typed code (decision for adoption). |
| `SourceTooLarge` | Input is above `MaxInputBytes` | `HeroCustomAvatarStorageService.cs:178-181` (`HeroAvatarFormat.MaxUploadBytes`) | `SessionBackgroundImageErrorCodes.FileTooLarge` (`SessionBackgroundImageDomainService.cs:341`, thrown at `SessionBackgroundImageAppService.cs:180-182`) | Hero: the existing "must be N MB or smaller" message. Session: `BusinessException` with `FileTooLarge`. The backend checks size first, so this is a safety net. |
| `DimensionsTooLarge` | Declared pixels are above `MaxPixels` | none | none | No existing message. Reuse the compression failure text until a dedicated message is decided. |
| `DestinationExists` | Destination exists and `overwrite` is false | none | none | Internal error. The backend writes to fresh GUID-based paths (`HeroCustomAvatarStorageService.cs:49`, `SessionBackgroundImageAppService.cs:266-269`). |
| `NativeRuntimeUnavailable` | Platform not supported, or the native library cannot load | `HeroAvatarAvifEncoder.UnavailableMessage` (`HeroAvatarAvifEncoder.cs:15-16`, thrown at `:48`). The text names ffmpeg and must be rewritten. | Not an error. Use the JPEG fallback (section 4.2). | Hero: `UserFriendlyException` with a rewritten message. Session: fall back to JPEG. |
| `EncodeFailed` | The encoder did not produce output | `HeroAvatarAvifEncoder.cs:110-112` | none | `UserFriendlyException("Hero avatar compression failed.")` (`HeroAvatarAvifEncoder.cs:111`). Session: keep the propagating path. |
| `OperationCanceledException` (not an `AvifConversionException`) | Cancelled | ffmpeg is killed and the token is rethrown (`HeroAvatarAvifEncoder.cs:96-105`) | `catch` cleans up and rethrows (`SessionBackgroundImageAppService.cs:102-106`) | Keep the existing cancellation path. |

### 6.2 Sample: error mapping

```csharp
using HagiCode.Libs.Imaging;
using PCode.Application;
using PCode.DomainServices;
using Volo.Abp;

namespace PCode.Application.Adoption;

public static class AvifErrorMapping
{
    // Hero avatars. Codes without a dedicated message fall back to the nearest existing one; other codes are rethrown as they are.
    public static Exception ToHeroException(AvifConversionException exception) => exception.ErrorCode switch
    {
        AvifErrorCode.NativeRuntimeUnavailable => new UserFriendlyException(HeroAvatarAvifEncoder.UnavailableMessage),
        AvifErrorCode.UnsupportedSourceFormat => new UserFriendlyException("Hero avatar only supports PNG, JPEG, WebP, and AVIF images."),
        AvifErrorCode.SourceTooLarge => new UserFriendlyException(
            $"Hero avatar file must be {HeroAvatarFormat.MaxUploadBytes / (1024 * 1024)} MB or smaller."),
        AvifErrorCode.CorruptSource => new UserFriendlyException("Hero avatar compression failed."),
        AvifErrorCode.EncodeFailed => new UserFriendlyException("Hero avatar compression failed."),
        AvifErrorCode.DimensionsTooLarge => new UserFriendlyException("Hero avatar compression failed."),
        _ => exception,
    };

    // Session backgrounds. Only the two existing typed codes are mapped; the rest propagate unchanged.
    public static Exception ToSessionException(AvifConversionException exception) => exception.ErrorCode switch
    {
        AvifErrorCode.UnsupportedSourceFormat => new BusinessException(
            SessionBackgroundImageErrorCodes.InvalidFileType,
            "Invalid file type. Only JPEG, PNG, WEBP, and GIF are supported."),
        AvifErrorCode.SourceTooLarge => new BusinessException(
            SessionBackgroundImageErrorCodes.FileTooLarge,
            "File size exceeds 10MB limit."),
        _ => exception,
    };
}
```

## 7. CLI AVIF code that becomes removable

These symbols exist only to run ffmpeg for AVIF. They are removed in the adoption change, after the comparison in section 10 passes.

| Symbol | Location | What it does | Removal condition |
| --- | --- | --- | --- |
| `HeroAvatarAvifEncoder.RenderAsync` | `src/PCode.Application/HeroAvatarAvifEncoder.cs:56-114` | Starts ffmpeg (`new Process` at `:64`, `process.Start()` at `:91`), waits, kills on cancel (`:96-105`), checks exit code and output (`:107-113`) | Replaced by the converter from section 5 (`IAvifConverter`, section 3.3) |
| `HeroAvatarAvifEncoder.BuildFilterGraph` | `src/PCode.Application/HeroAvatarAvifEncoder.cs:116-124` | The ffmpeg filter string | Replaced by `AvifResize` and `AvifAlphaMode.Flatten` (section 3.1) |
| `HeroAvatarAvifEncoder.EnsureCapabilityAsync` | `HeroAvatarAvifEncoder.cs:42-50` | Caches the libaom-av1 and avif-muxer check per executable | Replaced by `AvifRuntime.Probe()` |
| `HeroAvatarAvifEncoder.ProbeCapabilityAsync`, `RunListingAsync`, `ListsEntry` | `HeroAvatarAvifEncoder.cs:126-175` | Runs `ffmpeg -encoders` and `-muxers` and parses the listing | Same as above |
| `HeroAvatarAvifEncoder.CapabilityByExecutablePath`, `Libaom`, `AvifMuxer` | `HeroAvatarAvifEncoder.cs:19-22` | Probe cache and the encoder and muxer names | Same as above |
| `HeroAvatarAvifEncoder.ResolveFfmpegPath` | `HeroAvatarAvifEncoder.cs:24-36` | ffmpeg lookup through `IImageCompressionToolResolver.ResolveCandidates` (`:26-28`), with the message at `:32` | Not needed once AVIF no longer uses ffmpeg |
| `HeroAvatarAvifEncoder.FfmpegToolId` | `HeroAvatarAvifEncoder.cs:18` | `"ffmpeg"` lookup key | Same as above |
| `HeroAvatarFormat.FfmpegLookupContentType` | `src/PCode.Application/HeroAvatarFormat.cs:39` | The `image/png` matrix key used only to find ffmpeg | Same as above |
| `HeroAvatarAvifEncoder.CreateStartInfo`, `TryKill` | `HeroAvatarAvifEncoder.cs:177-202` | Process plumbing | Same as above |
| Callers | `HeroCustomAvatarStorageService.cs:46-47`, `:57`; `HeroCustomAvatarLegacyFormatMigrator.cs:64-65`, `:74` | Lookup, probe and render for upload and legacy upgrade | Replaced by the converter from section 5 (`IAvifConverter`) in both places |
| Test support | `tests/PCode.Application.Tests/HeroCustomAvatarLegacyFormatMigratorTests.cs:114`; `HeroCustomAvatarAvifTestSupport.cs:145` (fake ffmpeg script) | Real and stubbed ffmpeg runs in tests | Replace with converter-based fixtures |
| `HeroAvatarFormat.EncodeCrf`, `FlattenBackgroundColor` | `HeroAvatarFormat.cs:25`, `:33` | Source values for the mapping | Remove after the options in section 3.2 are the only source |

The `Speed` and `Quality` values in section 3.2 replace these constants, so their history stays in this guide.

## 8. What stays on the compression matrix

Non-AVIF recompression is outside this library and stays on `IImageCompressionToolResolver`:

| Stays | Location | Notes |
| --- | --- | --- |
| `IImageCompressionToolResolver`, `IImageCompressionExecutor` | `src/PCode.DomainServices/ImageCompressionContracts.cs:3`, `:8` | The matrix interface and executor |
| `ImageCompressionToolResolver.ToolMatrix` | `src/PCode.DomainServices/ImageCompressionToolResolver.cs:9-87` | The per-content-type matrix |
| `optipng`, `oxipng`, `pngcrush` (PNG) | `ImageCompressionToolResolver.cs:21-41` | Lossless PNG recompression |
| `jpegtran`, `jpegoptim` (JPEG) | `ImageCompressionToolResolver.cs:52-65` | Lossless JPEG recompression |
| `gifsicle` (GIF) | `ImageCompressionToolResolver.cs:69-75` | GIF optimisation |
| `ffmpeg` to WebP, three entries | `ImageCompressionToolResolver.cs:14-20` (PNG), `:45-51` (JPEG), `:79-85` (WebP) | These use `-c:v libwebp`, not AVIF. ffmpeg therefore stays a dependency of the compression matrix after the AVIF path is removed. |
| `SessionBackgroundImageAppService.TryCompressAsync` | `src/PCode.Application/Services/SessionBackgroundImageAppService.cs:186-235` | Runs the matrix for uploads |

The AVIF path shares one lookup key with these entries. The PNG ffmpeg entry at `ImageCompressionToolResolver.cs:14-20` is the key that `HeroAvatarAvifEncoder` used to find ffmpeg (`HeroAvatarFormat.FfmpegLookupContentType`). The entry itself is WebP and stays.

## 9. Calibration

### 9.1 Method

- **Hero baseline.** ffmpeg 8.1.3 with `libaom-av1` and the `avif` muxer, run with the arguments in section 2.1 (exact filter graph from `BuildFilterGraph`). The backend's ffmpeg build is not known. A different libaom version changes the baseline. Median of three runs of wall time, which includes process start.
- **Hero library.** `Cover`, `Flatten` onto `#64748b`, `Yuv420`, `Speed` 6, `Quality` 40 to 80, at sizes 64, 128 and 512. Median of three `ConvertAsync` runs, using `AvifConversionResult.Elapsed`. This includes decode, orientation, resize and encode.
- **Thumbnail baseline.** ImageSharp 3.1.12, the backend's pinned version (`Directory.Packages.props:50`), from the local NuGet cache. `Max`, 200x200, `JpegEncoder` quality 85. Median of three runs of clone, resize and save. Load time is excluded.
- **Thumbnail library.** `Fit` 200x200, `Flatten` onto `#64748b`, `Yuv420`, `Speed` 6. Qualities 40, 50 and 60 are the requested grid. 70, 80 and 90 were run because the grid did not reach the JPEG PSNR.
- **Reference.** Pillow 12.3.0 `LANCZOS` resample: a cover crop to the square for hero sources, and the exact ImageSharp output size for thumbnails. Flattened onto `#64748b`, 8-bit RGB. Pillow's resampler is independent of both encoders' resamplers.
- **Scoring.** Each output is decoded with NetVips (libvips 8.18.7, libheif) and compared with the reference over all R, G and B samples. PSNR is computed per image. The mean is taken over images.
- **Selection rules.** Hero: the lowest Quality where mean bytes are at most 1.10 times the baseline mean, and mean PSNR is at least the baseline mean minus 0.5 dB, over all six sources at 64, 128 and 512. Thumbnail: the lowest Quality in 40, 50 and 60 where mean PSNR is at least the JPEG mean.
- **Sources** (read-only, from the monorepo):

| Use | Source | Format and size |
| --- | --- | --- |
| Hero | `repos/newbe/static/images/Justin-Topuc-Recommendation.jpg` | JPEG, 1700x2338 |
| Hero | `repos/newbe/static/images/20200302-008.jpg` | JPEG, 1684x1080 |
| Hero | `repos/newbe/static/images/pindao.jpg` | JPEG, 1376x2446 |
| Hero | `hagicode-hero-1920x1080.png` | PNG, RGB, 1920x1080 |
| Hero | `repos/web/public/pwa/icon-512.png` | PNG, RGBA, 512x512 (alpha) |
| Hero | `repos/newbe/static/images/20200618-001.png` | PNG, palette with transparency, 601x441 (alpha) |
| Thumbnail | `repos/web/public/theme-backgrounds/school-1.webp` | WebP, RGB, 1024x1024 |
| Thumbnail | `repos/web/public/theme-backgrounds/space-travel-3.webp` | WebP, RGB, 1024x1024 |
| Thumbnail | `repos/newbe/static/images/20210717-001.jpg` | JPEG, 2527x1136 |
| Thumbnail | `repos/newbe/static/images/20200302-004.jpg` | JPEG, 1210x1080 |
| Thumbnail | `repos/newbe/static/images/20200302-002.jpg` | JPEG, 1634x1080 |
| Thumbnail | `repos/newbe/static/images/20171218-001.jpg` | JPEG, 1560x902 |

None of the sources carries an EXIF orientation tag, so orientation is not exercised by this calibration. The orientation behavior was checked separately with a synthetic file (section 4.1). The thumbnail sources are all opaque.

### 9.2 Hero avatar results

Baseline ffmpeg, pooled over 18 sample and size pairs: mean 6,895 bytes, mean PSNR 32.09 dB, mean wall time 219 ms.

| Quality | Mean bytes | Bytes vs ffmpeg | Mean PSNR (dB) | PSNR vs ffmpeg | Mean encode (ms) | Passes both rules |
| ---: | ---: | ---: | ---: | ---: | ---: | :--- |
| 40 | 3,860 | 0.560x | 29.79 | -2.30 | 36.1 | no |
| 50 | 5,568 | 0.808x | 31.85 | -0.24 | 37.0 | **yes, selected** |
| 60 | 7,676 | 1.113x | 33.59 | +1.49 | 39.4 | no (bytes) |
| 70 | 10,213 | 1.481x | 35.08 | +2.98 | 40.3 | no |
| 80 | 13,142 | 1.906x | 36.04 | +3.94 | 42.0 | no |

The pooled rule selects **Quality 50**. Split by size, the pooled choice is not uniform:

| Size | ffmpeg bytes / dB | Quality 50 bytes (vs ffmpeg) / dB (delta) | Quality 60 bytes (vs ffmpeg) / dB (delta) |
| ---: | ---: | ---: | ---: |
| 64 | 996 / 28.61 | 762 (0.765x) / 30.04 (+1.44) | 951 (0.955x) / 31.96 (+3.35) |
| 128 | 2,287 / 31.46 | 1,755 (0.768x) / 31.48 (+0.02) | 2,295 (1.004x) / 33.09 (+1.64) |
| 512 | 17,401 / 36.22 | 14,187 (0.815x) / 34.04 (-2.18) | 19,781 (1.137x) / 35.71 (-0.51) |

At 512 px no grid point passes both rules. Quality 60 is the closest, at 0.01 dB outside the PSNR floor and 13.7% above the ffmpeg size. Quality 50 is smaller, but 2.18 dB below the ffmpeg output on average.

One sample accounts for about half of the 512 gap: `hero-banner-palette-alpha` is 6.4 dB below ffmpeg at Quality 50, and section 9.4 explains why. Without it, the pooled rule selects **Quality 60** (1.100x bytes, +1.35 dB), and Quality 50 fails the PSNR floor (-0.64 dB). The choice of 50 therefore depends on that one sample.

Per sample and size. Bytes and PSNR for ffmpeg, then Quality 50 and 60:

| Sample | Size | ffmpeg bytes / dB | Q50 bytes / dB | Q60 bytes / dB |
| --- | ---: | ---: | ---: | ---: |
| hero-20200302-008 | 64 | 1,430 / 27.77 | 935 / 26.73 | 1,198 / 28.53 |
| hero-20200302-008 | 128 | 3,649 / 30.35 | 2,365 / 28.19 | 3,238 / 30.02 |
| hero-20200302-008 | 512 | 25,696 / 34.32 | 19,562 / 32.54 | 28,142 / 34.49 |
| hero-banner-palette-alpha | 64 | 548 / 26.22 | 666 / 33.33 | 801 / 34.26 |
| hero-banner-palette-alpha | 128 | 1,151 / 30.37 | 1,385 / 34.81 | 1,606 / 35.26 |
| hero-banner-palette-alpha | 512 | 5,793 / 34.92 | 6,176 / 28.55 | 7,500 / 28.62 |
| hero-justin-portrait | 64 | 643 / 27.12 | 534 / 34.37 | 684 / 37.20 |
| hero-justin-portrait | 128 | 1,679 / 31.50 | 1,311 / 33.96 | 1,769 / 36.26 |
| hero-justin-portrait | 512 | 23,349 / 36.49 | 17,153 / 33.72 | 22,738 / 36.95 |
| hero-pindao-tall | 64 | 878 / 27.32 | 624 / 29.44 | 768 / 30.92 |
| hero-pindao-tall | 128 | 2,118 / 30.47 | 1,594 / 30.58 | 2,162 / 31.65 |
| hero-pindao-tall | 512 | 13,685 / 35.99 | 11,466 / 35.64 | 15,154 / 36.99 |
| hero-png-1920 | 64 | 1,361 / 25.69 | 945 / 23.85 | 1,237 / 25.27 |
| hero-png-1920 | 128 | 3,470 / 27.28 | 2,554 / 25.65 | 3,374 / 26.83 |
| hero-png-1920 | 512 | 31,515 / 30.94 | 25,808 / 29.33 | 38,875 / 31.04 |
| hero-pwa-icon-alpha | 64 | 1,116 / 37.51 | 867 / 32.52 | 1,020 / 35.57 |
| hero-pwa-icon-alpha | 128 | 1,654 / 38.77 | 1,322 / 35.68 | 1,621 / 38.54 |
| hero-pwa-icon-alpha | 512 | 4,367 / 44.66 | 4,956 / 44.43 | 6,276 / 46.17 |

Per pair, Quality 50 is below the ffmpeg PSNR for 12 of the 18 pairs, by up to 6.4 dB (palette banner at 512 px; the next largest is 5.0 dB, PWA icon at 64 px). It is above for 6 pairs, by up to 7.3 dB (portrait photo at 64 px). The pooled mean passes because the positive pairs offset the negative ones. Treat the rule result as a starting point, and check the adopter's own uploads (section 10, step 2).

### 9.3 Thumbnail results

Baseline: ImageSharp JPEG quality 85, mean 8,609 bytes, mean PSNR 33.55 dB, mean encode 7.1 ms (load excluded). Library: mean encode time includes decode.

| Quality | In requested grid | Mean bytes | Bytes vs JPEG | Mean PSNR (dB) | PSNR vs JPEG | Mean encode (ms) | PSNR at least JPEG |
| ---: | :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| 40 | yes | 2,078 | 0.241x | 29.37 | -4.19 | 28.1 | no |
| 50 | yes | 3,024 | 0.351x | 31.19 | -2.36 | 29.0 | no |
| 60 | yes | 4,280 | 0.497x | 32.82 | -0.73 | 30.2 | no |
| 70 | extension | 5,769 | 0.670x | 34.09 | +0.53 | 30.7 | **yes, selected** |
| 80 | extension | 7,456 | 0.866x | 35.02 | +1.46 | 31.9 | yes |
| 90 | extension | 10,354 | 1.203x | 35.85 | +2.29 | 34.8 | yes |

No point in the requested grid reaches the JPEG PSNR. The lowest point that does is **Quality 70**, which is outside the grid. At Quality 70 the AVIF thumbnail is smaller than the JPEG (0.67x). Quality 60 is the alternative if size matters more than PSNR: 0.50x the bytes at -0.73 dB.

Per sample at Quality 70 against the JPEG (bytes, then PSNR):

| Sample | JPEG bytes / dB | Q70 bytes / dB |
| --- | ---: | ---: |
| thumb-20171218-001 | 4,954 / 35.48 | 2,819 / 36.11 |
| thumb-20200302-002 | 8,625 / 31.70 | 5,968 / 32.13 |
| thumb-20200302-004 | 8,633 / 36.00 | 5,437 / 35.87 |
| thumb-20210717-001 | 5,443 / 32.33 | 3,723 / 32.91 |
| thumb-school-1 | 10,633 / 33.55 | 6,933 / 34.29 |
| thumb-space-travel-3 | 13,368 / 32.27 | 9,733 / 33.23 |

Five of six samples are at or above the JPEG PSNR at Quality 70. `thumb-20200302-004` is 0.13 dB below it, and is smaller by 37%.

### 9.4 Deviations and sensitivity

- **Upscaling on sharp line art.** `hero-banner-palette-alpha` is 601x441, so `Cover` at 512 px is an upscale of about 1.16x. Resampling alone, before any encode, gives 28.7 dB against the reference at 512 px, and 44.3 dB at 64 px. The gap is in the resampler, not the encoder: libvips' upscale differs from the two references (Pillow LANCZOS, ffmpeg bicubic) on the sharp black and white edges in this image. The output looks the same to the eye. The exact mechanism, such as the kernel phase on upscale, was not isolated. Sources smaller than the square are upscaled by `Cover`, so adopters should compare those outputs visually.
- **Encode time is not like-for-like.** The ffmpeg figure includes process start. The ImageSharp figure excludes load. The library figure includes decode.
- **Speed.** The library's `Speed` 6 maps to libheif `effort` 3. The equivalence to libaom `cpu-used` 6 was not verified, so the calibration measures the outcome at `Speed` 6, not the parameter.
- **Transparent thumbnails.** No calibration source for a thumbnail has alpha. The flatten colour for thumbnails is a policy choice.
- **Reference bias.** The reference is a third resampler, not ground truth. The libvips resample alone scores 44.3 dB at 64 px and 43.6 dB at 128 px against the Pillow reference (palette banner, no encode). That is far above the encode loss at these qualities, so the reference does not drive the selection at small sizes. At 512 px the same comparison scores 28.7 dB, which is the upscale gap above.

## 10. Rollout plan

1. **Adopt the package with no behavior change.** Add the version and the `PackageReference` (section 5.1) and register the converter (section 5.2). Log `AvifRuntime.Probe()` at startup. The ffmpeg path stays active.
2. **Compare hero output side by side.** For each size, encode the same uploads with the ffmpeg path and with the converter from section 5 (`IAvifConverter`). Keep both outputs. Measure bytes and PSNR against the source with the method in section 9.1, and review the outputs visually, especially small sources and sharp edges (section 9.4). Adopt Quality 50 if the same rules hold on the adopter's own uploads. If the 512 px loss is not acceptable, use Quality 60 for that size.
3. **Switch hero avatars to the converter.** Replace `RenderAsync` at the two call sites (section 3.3). Replace the error messages in section 6.1, including the wording of `UnavailableMessage`, which names ffmpeg. Update the legacy upgrade in `HeroCustomAvatarLegacyFormatMigrator`, which calls the same encoder.
4. **Compare thumbnails.** Encode the same uploads at `Quality` 70 and with the JPEG path. Check the decode-and-score numbers in section 9.3 on the adopter's uploads. Check the `Width` and `Height` change for EXIF-rotated uploads (section 4.1).
5. **Switch thumbnails, together.** Ship the `.thumb.avif` naming, the `ResolveStoragePaths` change and the content-type change in `GetContentAsync` in one step, with the JPEG fallback in place. Legacy `.thumb.jpg` records need no migration.
6. **Remove the CLI AVIF path.** After steps 2 to 5 pass, delete the symbols in section 7 and their tests. Keep the compression matrix (section 8). Its ffmpeg WebP entries are not part of this removal.
7. **Rollback.** Until step 6, the ffmpeg path is still in the code. Reverting steps 3 and 5 restores the previous behavior, and the old AVIF files stay valid because their extension and content type do not change.

Existing hero avatars are AVIF files, and `HeroAvatarFormat.IsAvifAssetPath` (`HeroAvatarFormat.cs:62`) keeps recognizing them. The library does not re-encode them.

## 11. Notes

- The library never calls ffmpeg, avifenc or any other executable, in any phase. The reader of this guide is never asked to install or set up an external tool for AVIF.
- `StripMetadata` (default `true`) removes EXIF, GPS and other embedded metadata. EXIF orientation is applied to the pixels before this happens. **Whether `StripMetadata` also drops the ICC profile has not been verified.** Wide-gamut sources must be checked for a colour shift during adoption. Do not assume either way.
- ImageSharp 3.1.12 has open advisories. NuGet reported them during the calibration restore: GHSA-gwg2-r3hj-4w44, GHSA-wmxv-xphr-5c9g, GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q and GHSA-jjfr-hcj7-qf5w. The ImageSharp dependency can be dropped only after the JPEG fallback in section 4.2 is removed.
- The library's `Cover` upscale differs from the other resamplers on sharp edges (section 9.4).
- The process-wide libvips settings are described in section 5.2.
