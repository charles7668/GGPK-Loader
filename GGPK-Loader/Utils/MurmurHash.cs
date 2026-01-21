using System;
using System.Buffers.Binary;

namespace GGPK_Loader.Utils;

public static class MurmurHash
{
    public static uint MurmurHash2(ReadOnlySpan<byte> data, uint seed)
    {
        const uint m = 0x5BD1E995u;
        const int r = 24;

        unchecked
        {
            seed ^= (uint)data.Length;

            while (data.Length >= 4)
            {
                var k = BinaryPrimitives.ReadUInt32LittleEndian(data);
                k *= m;
                k ^= k >> r;
                k *= m;

                seed = (seed * m) ^ k;

                data = data[4..];
            }

            switch (data.Length)
            {
                case 3:
                    seed ^= (uint)data[2] << 16;
                    goto case 2;
                case 2:
                    seed ^= (uint)data[1] << 8;
                    goto case 1;
                case 1:
                    seed ^= data[0];
                    seed *= m;
                    break;
            }

            seed ^= seed >> 13;
            seed *= m;
            return seed ^ (seed >> 15);
        }
    }

    public static ulong MurmurHash64A(ReadOnlySpan<byte> utf8Name, ulong seed = 0x1337B33F)
    {
        if (utf8Name.IsEmpty)
        {
            return 0xF42A94E69CFF42FEul;
        }

        if (utf8Name[^1] == '/')
        {
            utf8Name = utf8Name[..^1];
        }

        const ulong m = 0xC6A4A7935BD1E995ul;
        const int r = 47;

        unchecked
        {
            seed ^= (ulong)utf8Name.Length * m;

            while (utf8Name.Length >= 8)
            {
                var k = BinaryPrimitives.ReadUInt64LittleEndian(utf8Name);
                k *= m;
                k ^= k >> r;
                k *= m;

                seed ^= k;
                seed *= m;

                utf8Name = utf8Name[8..];
            }

            if (utf8Name.Length > 0)
            {
                ulong tail = 0;
                for (var i = 0; i < utf8Name.Length; i++)
                {
                    tail |= (ulong)utf8Name[i] << (i * 8);
                }

                seed ^= tail;
                seed *= m;
            }

            seed ^= seed >> r;
            seed *= m;
            return seed ^ (seed >> r);
        }
    }
}