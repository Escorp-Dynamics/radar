namespace Radar.Services;

/// <summary>
/// Формат подложки RBM2 (tools/build_basemap.py): координаты — целые в единицах 1e-5°, первая точка части абсолютно
/// (2 × i32: широта, долгота), остальные — дельты varint-zigzag. Общий декодер для слоя карты и справочника районов.
/// </summary>
public static class BasemapFormat
{
    public const string Magic = "RBM2";
    private const double Q = 1e-5;

    /// <summary>Читает часть (контур) из <paramref name="lat"/>.Length точек: широты и долготы в градусах.</summary>
    public static void ReadPart(ReadOnlySpan<byte> d, ref int pos, Span<double> lat, Span<double> lon)
    {
        int la = BitConverter.ToInt32(d.Slice(pos, 4)), lo = BitConverter.ToInt32(d.Slice(pos + 4, 4));
        pos += 8;
        if (lat.Length == 0) return;
        lat[0] = la * Q;
        lon[0] = lo * Q;
        for (int i = 1; i < lat.Length; i++)
        {
            la += (int)Varint(d, ref pos);
            lo += (int)Varint(d, ref pos);
            lat[i] = la * Q;
            lon[i] = lo * Q;
        }
    }

    /// <summary>Пропускает часть из <paramref name="n"/> точек, не декодируя координаты.</summary>
    public static void SkipPart(ReadOnlySpan<byte> d, ref int pos, int n)
    {
        pos += 8;
        for (int i = 1; i < n; i++)
            for (int k = 0; k < 2; k++)
                while ((d[pos++] & 0x80) != 0) { }
    }

    private static long Varint(ReadOnlySpan<byte> d, ref int pos)
    {
        ulong v = 0;
        int shift = 0;
        byte b;
        do
        {
            b = d[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return (long)(v >> 1) ^ -(long)(v & 1);
    }
}
