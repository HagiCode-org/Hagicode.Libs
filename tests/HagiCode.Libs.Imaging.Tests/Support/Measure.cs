using HagiCode.Libs.Imaging.Engine;
using NetVips;

namespace HagiCode.Libs.Imaging.Tests.Support;

/// <summary>Decoding and similarity helpers for the fidelity tests. They use the engine's own loader.</summary>
internal static class Measure
{
    /// <summary>Decodes to 8-bit RGB (alpha dropped) so two images can be compared pixel by pixel.</summary>
    internal static (int Width, int Height, int Bands, byte[] Rgb) DecodeRgb(byte[] bytes, AvifSourceFormat format)
    {
        using var image = LibvipsAvifEngine.Load(bytes, format, null);
        using var rgb = image.HasAlpha() ? image.ExtractBand(0, n: 3) : image.Copy();
        return (rgb.Width, rgb.Height, image.Bands, rgb.WriteToMemory());
    }

    /// <summary>Peak signal-to-noise ratio in dB over the raw 8-bit samples. Identical inputs return positive infinity.</summary>
    internal static double Psnr(byte[] reference, byte[] candidate)
    {
        if (reference.Length != candidate.Length)
        {
            throw new ArgumentException("Images must have the same sample count.");
        }

        double squaredError = 0;
        for (var i = 0; i < reference.Length; i++)
        {
            var difference = reference[i] - candidate[i];
            squaredError += difference * difference;
        }

        if (squaredError == 0)
        {
            return double.PositiveInfinity;
        }

        var mse = squaredError / reference.Length;
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }
}
