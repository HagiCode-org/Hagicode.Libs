namespace HagiCode.Libs.Imaging.Internal;

/// <summary>
/// Five embedded 8x8 images, one per supported source format, used by the runtime probe to prove that every loader
/// decodes on this machine. Generated once for gate G1 (2026-10-14); the AVIF fixture was encoded with the libvips
/// engine. Do not edit by hand.
/// </summary>
internal static class ProbeFixtures
{
    /// <summary>8x8 Png fixture.</summary>
    internal static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAGUlEQVR42mNkYGhQYGDARCwMCgxYweCUAABobwJednxjlwAAAABJ" +
        "RU5ErkJggg=="
    );

    /// <summary>8x8 Jpeg fixture.</summary>
    internal static readonly byte[] Jpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMU" +
        "FRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQU" +
        "FBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF" +
        "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW" +
        "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi" +
        "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
        "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm" +
        "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq" +
        "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDmfhR+yp/qf9D9P4aKKK+RzbifM/rT/eHpeHvGOcf2FS/e/wBWXmf/2Q=="
    );

    /// <summary>8x8 WebP fixture.</summary>
    internal static readonly byte[] WebP = Convert.FromBase64String(
        "UklGRmQAAABXRUJQVlA4IFgAAABwAgCdASoIAAgAAMASJbACdLoAyv8D0TLS4hgAAP7/EeF51L/0KhgHslaOl1fgRn5nyH1FwA3+" +
        "dWv47tp/cp//+RX/gjyOn/33Y/+B7N/9lP/4+sFQAAAA"
    );

    /// <summary>8x8 Gif fixture.</summary>
    internal static readonly byte[] Gif = Convert.FromBase64String(
        "R0lGODdhCAAIAIUAAODggMDggODAgMDAgKDggKDAgOCggMCggKCggIDggGDggIDAgGDAgICggGCggEDggCDggEDAgCDAgADggADA" +
        "gECggCCggACggOCAgMCAgOBggMBggKCAgKBggICAgGCAgIBggGBggECAgCCAgEBggCBggACAgABggOBAgMBAgOAggMAggKBAgKAg" +
        "gOAAgMAAgKAAgIBAgGBAgIAggGAggIAAgGAAgEBAgCBAgEAggCAggABAgAAggEAAgCAAgAAAgCwAAAAACAAIAAAISwB/+OhhowaM" +
        "Fy546MhBY0aLFSp24LghIwaLFChOlCARAkSHDRpMjBDxwQOHDBguWKjgoAGCAwYoSIjAYEGBAQImQHigIAGBAAACAgA7"
    );

    /// <summary>8x8 Avif fixture.</summary>
    internal static readonly byte[] Avif = Convert.FromBase64String(
        "AAAAHGZ0eXBhdmlmAAAAAG1pZjFhdmlmbWlhZgAAANZtZXRhAAAAAAAAACFoZGxyAAAAAAAAAABwaWN0AAAAAAAAAAAAAAAAAAAA" +
        "ACJpbG9jAAAAAERAAAEAAQAAAAAA+gABAAAAAAAAACEAAAAjaWluZgAAAAAAAQAAABVpbmZlAgAAAAABAABhdjAxAAAAAA5waXRt" +
        "AAAAAAABAAAAVmlwcnAAAAA4aXBjbwAAAAxhdjFDgQAMAAAAABRpc3BlAAAAAAAAAAgAAAAIAAAAEHBpeGkAAAAAAwgICAAAABZp" +
        "cG1hAAAAAAAAAAEAAQOBAgMAAAApbWRhdBIACggYCL9ogIaDQjITGUeHhiGHnnnmgkAAkEDJHGFCvg=="
    );
}
