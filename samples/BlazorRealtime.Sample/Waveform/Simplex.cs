namespace BlazorRealtime.Sample.Waveform;

/// <summary>3D simplex noise (Perlin's simplex algorithm), −1..1, with a fixed permutation so every run looks the same.</summary>
internal static class Simplex
{
    private const float F3 = 1f / 3f, G3 = 1f / 6f;
    private static readonly byte[] Perm = new byte[512], PermMod12 = new byte[512];

    static Simplex()
    {
        var p = new byte[256];
        for (var i = 0; i < 256; i++) p[i] = (byte)i;
        new Random(1979).Shuffle(p); // Unknown Pleasures' year
        for (var i = 0; i < 512; i++)
        {
            Perm[i] = p[i & 255];
            PermMod12[i] = (byte)(Perm[i] % 12);
        }
    }

    private static ReadOnlySpan<sbyte> Grad3 =>
    [
        1, 1, 0, -1, 1, 0, 1, -1, 0, -1, -1, 0, 1, 0, 1, -1, 0, 1, 1, 0, -1, -1, 0, -1, 0, 1, 1, 0, -1, 1, 0, 1, -1, 0,
        -1, -1
    ];

    public static float Noise(float x, float y, float z)
    {
        var s = (x + y + z) * F3;
        int i = (int)MathF.Floor(x + s), j = (int)MathF.Floor(y + s), k = (int)MathF.Floor(z + s);
        var t = (i + j + k) * G3;
        float x0 = x - (i - t), y0 = y - (j - t), z0 = z - (k - t);
        int i1, j1, k1, i2, j2, k2; // which simplex of the cube: offsets of corners 1 and 2
        if (x0 >= y0)
        {
            if (y0 >= z0)
            {
                i1 = 1;
                j1 = 0;
                k1 = 0;
                i2 = 1;
                j2 = 1;
                k2 = 0;
            }
            else if (x0 >= z0)
            {
                i1 = 1;
                j1 = 0;
                k1 = 0;
                i2 = 1;
                j2 = 0;
                k2 = 1;
            }
            else
            {
                i1 = 0;
                j1 = 0;
                k1 = 1;
                i2 = 1;
                j2 = 0;
                k2 = 1;
            }
        }
        else
        {
            if (y0 < z0)
            {
                i1 = 0;
                j1 = 0;
                k1 = 1;
                i2 = 0;
                j2 = 1;
                k2 = 1;
            }
            else if (x0 < z0)
            {
                i1 = 0;
                j1 = 1;
                k1 = 0;
                i2 = 0;
                j2 = 1;
                k2 = 1;
            }
            else
            {
                i1 = 0;
                j1 = 1;
                k1 = 0;
                i2 = 1;
                j2 = 1;
                k2 = 0;
            }
        }

        int ii = i & 255, jj = j & 255, kk = k & 255;
        return 32f * (Corner(PermMod12[ii + Perm[jj + Perm[kk]]], x0, y0, z0)
                      + Corner(PermMod12[ii + i1 + Perm[jj + j1 + Perm[kk + k1]]], x0 - i1 + G3, y0 - j1 + G3,
                          z0 - k1 + G3)
                      + Corner(PermMod12[ii + i2 + Perm[jj + j2 + Perm[kk + k2]]], x0 - i2 + 2 * G3, y0 - j2 + 2 * G3,
                          z0 - k2 + 2 * G3)
                      + Corner(PermMod12[ii + 1 + Perm[jj + 1 + Perm[kk + 1]]], x0 - 1 + 3 * G3, y0 - 1 + 3 * G3,
                          z0 - 1 + 3 * G3));
    }

    private static float Corner(int g, float x, float y, float z)
    {
        var t = 0.6f - x * x - y * y - z * z;
        if (t < 0) return 0;
        var grad = Grad3;
        t *= t;
        return t * t * (grad[g * 3] * x + grad[g * 3 + 1] * y + grad[g * 3 + 2] * z);
    }
}