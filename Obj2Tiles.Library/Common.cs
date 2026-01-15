using Obj2Tiles.Library.Geometry;
using Obj2Tiles.Library.Materials;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Obj2Tiles.Library;

public static class Common
{
    public static readonly double Epsilon = double.Epsilon * 10;

    public static void CopyImage(Image<Rgba32> sourceImage, Image<Rgba32> dest, int sourceX, int sourceY,
        int sourceWidth, int sourceHeight, int destX, int destY)
    {
        var height = sourceHeight;

        sourceImage.ProcessPixelRows(dest, (sourceAccessor, targetAccessor) =>
        {
            var shouldCulaSourceAccessorIndex = sourceY + height > sourceAccessor.Height;

            for (var i = 0; i < height; i++)
            {
                var sourceAccessorIndex = sourceY + i;
                if (shouldCulaSourceAccessorIndex && sourceAccessorIndex >= sourceAccessor.Height)
                {
                    sourceAccessorIndex %= sourceAccessor.Height;
                }

                var sourceRow = sourceAccessor.GetRowSpan(sourceAccessorIndex);
                var targetRow = targetAccessor.GetRowSpan(i + destY);

                var shouldCulaSourceRowIndex = sourceX + sourceWidth > sourceRow.Length;
                for (var x = 0; x < sourceWidth; x++)
                {
                    var sourceRowIndex = x + sourceX;
                    if (shouldCulaSourceRowIndex && sourceRowIndex >= sourceRow.Length)
                    {
                        sourceRowIndex %= sourceRow.Length;
                    }

                    targetRow[x + destX] = sourceRow[sourceRowIndex]; // 
                }
            }
        });
    }

    private const double SRGB_THRESHOLD = 0.04045;
    private const double SRGB_LINEAR_SCALE = 12.92;
    private const double SRGB_OFFSET = 0.055;
    private const double SRGB_SCALE = 1.055;
    private const double SRGB_GAMMA = 2.4;

    /// <summary>
    /// Converts an sRGB channel value to linear RGB.
    /// </summary>
    /// <remarks>
    /// Formula reference:
    /// https://spitzak.github.io/conversion/srgb.html
    /// </remarks>
    static double SrgbToLinear(byte cByte)

    {
        var c = cByte / 255.0;
        if (c <= SRGB_THRESHOLD)
            return c / SRGB_LINEAR_SCALE;

        return Math.Pow((c + SRGB_OFFSET) / SRGB_SCALE, SRGB_GAMMA);
    }

    public static RGB ConvertToRGB(Rgba32 color)
    {
        return new RGB(
            SrgbToLinear(color.R),
            SrgbToLinear(color.G),
            SrgbToLinear(color.B)
        );
    }

    public static double Area(Vertex2 a, Vertex2 b, Vertex2 c)
    {
        return Math.Abs(
            (a.X - c.X) * (b.Y - a.Y) -
            (a.X - b.X) * (c.Y - a.Y)
        ) / 2;
    }

    public static Vertex3 Orientation(Vertex3 a, Vertex3 b, Vertex3 c)
    {
        // Calculate triangle orientation
        var v0 = b - a;
        var v1 = c - a;
        var v2 = v0.Cross(v1);
        return v2;
    }

    public static int NextPowerOfTwo(int x)
    {
        x--;
        x |= (x >> 1);
        x |= (x >> 2);
        x |= (x >> 4);
        x |= (x >> 8);
        x |= (x >> 16);
        return (x + 1);
    }

    public static int PreviousPowerOfTwo(int x)
    {
        if (x <= 1) return 0;

        x |= (x >> 1);
        x |= (x >> 2);
        x |= (x >> 4);
        x |= (x >> 8);
        x |= (x >> 16);

        return x - (x >> 1);
    }

    public static int ClosestPowerOfTwo(int x)
    {
        var lowerPower = PreviousPowerOfTwo(x);

        if (lowerPower == x)
            return lowerPower;
        
        var upperPower = NextPowerOfTwo(x);
        return (x - lowerPower < upperPower - x) ? lowerPower : upperPower;
    }

    /// <summary>
    /// Gets the distance of P from A (in percent) relative to segment AB
    /// </summary>
    /// <param name="a">Edge start</param>
    /// <param name="b">Edge end</param>
    /// <param name="p">Point on the segment</param>
    /// <returns></returns>
    public static double GetIntersectionPerc(Vertex3 a, Vertex3 b, Vertex3 p)
    {
        var edge1Length = a.Distance(b);
        var subEdge1Length = a.Distance(p);
        return subEdge1Length / edge1Length;
    }
}

public class FormattingStreamWriter : StreamWriter
{
    public FormattingStreamWriter(string path, IFormatProvider formatProvider)
        : base(path)
    {
        FormatProvider = formatProvider;
    }

    public override IFormatProvider FormatProvider { get; }
}