using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Sims4ModDoctor.Core
{
    public sealed class DbpfIndexEntry
    {
        public uint Type { get; init; }
        public uint Group { get; init; }
        public uint InstanceHi { get; init; }
        public uint InstanceLo { get; init; }
        public uint Offset { get; init; }
        public uint CompressedSize { get; init; }
        public uint UncompressedSize { get; init; }
        public ushort Compression { get; init; }
    }

    /// <summary>
    /// Sims 4 DBPF 2.x 索引与资源读取公共层。
    /// </summary>
    public static class DbpfReader
    {
        public const ushort CompressionZlib = 0x5A42;
        public const ushort CompressionNone = 0x0000;

        public static List<DbpfIndexEntry>? ReadIndex(Stream stream)
        {
            if (stream.Length < 96) return null;

            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            stream.Seek(0, SeekOrigin.Begin);

            byte[] magic = reader.ReadBytes(4);
            if (magic.Length < 4 || magic[0] != 'D' || magic[1] != 'B' || magic[2] != 'P' || magic[3] != 'F')
                return null;

            uint major = reader.ReadUInt32();
            reader.ReadUInt32(); // minor
            if (major != 2) return null;

            stream.Seek(0x24, SeekOrigin.Begin);
            uint indexCount = reader.ReadUInt32();
            uint indexOffsetShort = reader.ReadUInt32();
            reader.ReadUInt32(); // index size
            stream.Seek(0x40, SeekOrigin.Begin);
            ulong indexOffsetLong = reader.ReadUInt64();

            long indexPosition = indexOffsetShort != 0 ? indexOffsetShort : (long)indexOffsetLong;
            if (indexCount == 0 || indexPosition <= 0 || indexPosition >= stream.Length)
                return null;

            stream.Seek(indexPosition, SeekOrigin.Begin);
            uint indexFlags = reader.ReadUInt32();
            bool constantType = (indexFlags & 0x1) != 0;
            bool constantGroup = (indexFlags & 0x2) != 0;
            bool constantInstanceHi = (indexFlags & 0x4) != 0;

            uint headerType = 0, headerGroup = 0, headerInstanceHi = 0;
            if (constantType) headerType = reader.ReadUInt32();
            if (constantGroup) headerGroup = reader.ReadUInt32();
            if (constantInstanceHi) headerInstanceHi = reader.ReadUInt32();

            var entries = new List<DbpfIndexEntry>((int)Math.Min(indexCount, 100_000));
            for (uint i = 0; i < indexCount; i++)
            {
                uint type = constantType ? headerType : reader.ReadUInt32();
                uint group = constantGroup ? headerGroup : reader.ReadUInt32();
                uint instanceHi = constantInstanceHi ? headerInstanceHi : reader.ReadUInt32();
                uint instanceLo = reader.ReadUInt32();
                uint resourceOffset = reader.ReadUInt32();
                uint sizeField = reader.ReadUInt32();
                bool extended = (sizeField & 0x80000000) != 0;
                uint compressedSize = sizeField & 0x7FFFFFFF;
                uint uncompressedSize = reader.ReadUInt32();

                ushort compression = CompressionNone;
                if (extended)
                {
                    compression = reader.ReadUInt16();
                    reader.ReadUInt16(); // committed
                }

                entries.Add(new DbpfIndexEntry
                {
                    Type = type,
                    Group = group,
                    InstanceHi = instanceHi,
                    InstanceLo = instanceLo,
                    Offset = resourceOffset,
                    CompressedSize = compressedSize,
                    UncompressedSize = uncompressedSize,
                    Compression = compression
                });
            }

            return entries;
        }

        public static byte[]? ReadResource(Stream stream, DbpfIndexEntry entry, int maxBytes = 8 * 1024 * 1024)
        {
            if (entry.UncompressedSize == 0 || entry.UncompressedSize > maxBytes) return null;
            if (entry.Offset == 0 || entry.Offset + entry.CompressedSize > stream.Length) return null;

            stream.Seek(entry.Offset, SeekOrigin.Begin);
            byte[] raw = new byte[entry.CompressedSize];
            int read = stream.Read(raw, 0, raw.Length);
            if (read != raw.Length) return null;

            if (entry.Compression == CompressionNone || entry.CompressedSize == entry.UncompressedSize)
                return raw;

            if (entry.Compression == CompressionZlib)
                return TryDecompressZlib(raw, (int)entry.UncompressedSize);

            return raw;
        }

        public static byte[]? TryDecompressZlib(byte[] compressed, int expectedSize)
        {
            try
            {
                using var input = new MemoryStream(compressed);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(expectedSize > 0 ? expectedSize : 4096);
                zlib.CopyTo(output);
                return output.ToArray();
            }
            catch
            {
                try
                {
                    using var input = new MemoryStream(compressed);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream(expectedSize > 0 ? expectedSize : 4096);
                    deflate.CopyTo(output);
                    return output.ToArray();
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
