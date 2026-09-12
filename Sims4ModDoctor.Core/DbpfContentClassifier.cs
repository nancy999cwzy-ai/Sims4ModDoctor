using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sims4ModDoctor.Core
{
    /// <summary>
    /// 基于 DBPF 资源内容（CAS Part BodyType / Object Definition）的精确分类器。
    /// </summary>
    public static class DbpfContentClassifier
    {
        // 标准 CAS Part；同时兼容用户提供的备选 Type ID
        public const uint CasPartType = 0x034AEECB;
        public const uint CasPartTypeAlt = 0x03B33DDF;
        public const uint ObjectDefinitionType = 0xC0DB5AE7;

        /// <summary>
        /// 尝试从 DBPF 内容精确分类。未命中 CAS Part BodyType / Object Definition 时返回 false（交由文件名退路）。
        /// </summary>
        public static bool TryClassifyFromDbpf(string packagePath, out ModCategory category)
        {
            category = ModCategory.General;
            try
            {
                using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var index = DbpfReader.ReadIndex(fs);
                if (index == null || index.Count == 0) return false;

                bool hasObjectDef = index.Any(e => e.Type == ObjectDefinitionType);
                var bodyTypeVotes = new Dictionary<ModCategory, int>();

                foreach (var entry in index)
                {
                    if (entry.Type != CasPartType && entry.Type != CasPartTypeAlt)
                        continue;

                    long resume = fs.Position;
                    try
                    {
                        byte[]? data = DbpfReader.ReadResource(fs, entry, maxBytes: 2 * 1024 * 1024);
                        if (data == null || data.Length < 32) continue;

                        if (TryReadBodyType(data, out int bodyType))
                        {
                            var cat = MapBodyType(bodyType);
                            // MapBodyType 对未知 BodyType 可能返回 General，不计入有效票
                            if (cat == ModCategory.General) continue;
                            bodyTypeVotes[cat] = bodyTypeVotes.GetValueOrDefault(cat) + 1;
                        }
                    }
                    catch
                    {
                        // 单条 CASP 解析失败不影响其余资源
                    }
                    finally
                    {
                        fs.Position = resume;
                    }
                }

                if (bodyTypeVotes.Count > 0)
                {
                    category = bodyTypeVotes.OrderByDescending(kv => kv.Value).First().Key;
                    return true;
                }

                if (hasObjectDef)
                {
                    category = ModCategory.BuildBuy;
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        // 兼容旧调用：未命中时返回 General（新逻辑请用 TryClassifyFromDbpf）
        public static ModCategory ClassifyPackage(string packagePath)
            => TryClassifyFromDbpf(packagePath, out var cat) ? cat : ModCategory.General;

        public static ModCategory MapBodyType(int bodyType)
        {
            // BodyType: Hair=2, Hat=3, Top=5, Bottom=6, FullBody=7, Shoes=8, Acc/Skin/Makeup=9+
            return bodyType switch
            {
                0x02 => ModCategory.CAS_Hair,
                0x03 => ModCategory.CAS_Hat,
                0x05 or 0x06 or 0x07 => ModCategory.CAS_Clothing, // Top / Bottom / FullBody
                0x08 => ModCategory.CAS_Shoes,
                // Acc：眼镜/耳环/项链/手镯/戒指/手套/袜子/纹身等
                0x0A or 0x0B or 0x0C or 0x0D or 0x0E or 0x0F
                    or 0x10 or 0x11 or 0x12 or 0x14 or 0x15
                    or 0x18 or 0x19 or 0x1A or 0x1B or 0x1C
                    => ModCategory.CAS_Accessories,
                // Skin / Makeup / Face detail
                0x09 or 0x13 or 0x16 or 0x17 or 0x1D or 0x1E or 0x1F
                    or 0x20 or 0x21 or 0x22 or 0x23 or 0x24
                    => ModCategory.CAS_Accessories,
                _ => ModCategory.General
            };
        }

        /// <summary>
        /// 按 Sims4Tools CASPartResource 布局解析 BodyType。
        /// </summary>
        public static bool TryReadBodyType(byte[] data, out int bodyType)
        {
            bodyType = 0;
            try
            {
                using var ms = new MemoryStream(data);
                using var r = new BinaryReader(ms);

                uint version = r.ReadUInt32();
                r.ReadUInt32(); // TGI offset
                uint presetCount = r.ReadUInt32();
                if (presetCount != 0)
                    return TryScanBodyTypeFallback(data, out bodyType);

                if (!TryReadBigEndianUnicodeString(r))
                    return TryScanBodyTypeFallback(data, out bodyType);

                r.ReadSingle();   // sortPriority
                r.ReadUInt16();   // secondarySortIndex
                r.ReadUInt32();   // propertyID / OutfitID
                r.ReadUInt32();   // auralMaterialHash
                r.ReadByte();     // parmFlags
                r.ReadUInt64();   // excludePartFlags

                if (version >= 36)
                    r.ReadUInt64(); // excludeModifierRegionFlags
                else
                    r.ReadUInt32();

                // Flag list: count + pairs
                if (!TrySkipFlagList(r, version))
                    return TryScanBodyTypeFallback(data, out bodyType);

                r.ReadUInt32(); // deprecatedPrice
                r.ReadUInt32(); // partTitleKey
                r.ReadUInt32(); // partDescKey
                r.ReadByte();   // uniqueTextureSpace

                if (ms.Position + 8 > ms.Length)
                    return TryScanBodyTypeFallback(data, out bodyType);

                bodyType = r.ReadInt32();
                r.ReadInt32(); // bodySubType / unused

                // BodyType 合理范围校验（Sims 4 BodyType 枚举大约 1..0x50）
                if (bodyType < 0 || bodyType > 0x80)
                    return TryScanBodyTypeFallback(data, out bodyType);

                return true;
            }
            catch
            {
                return TryScanBodyTypeFallback(data, out bodyType);
            }
        }

        private static bool TryReadBigEndianUnicodeString(BinaryReader r)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) return false;
            byte charCount = r.ReadByte();
            int byteLen = charCount * 2;
            if (r.BaseStream.Position + byteLen > r.BaseStream.Length) return false;
            r.ReadBytes(byteLen);
            return true;
        }

        private static bool TrySkipFlagList(BinaryReader r, uint version)
        {
            if (r.BaseStream.Position + 4 > r.BaseStream.Length) return false;
            uint count = r.ReadUInt32();
            if (count > 10_000) return false;

            // version >= 37：通常为 uint32+uint32；更早为 uint16+uint16
            long bytesNeeded = version >= 37 ? count * 8L : count * 4L;
            if (r.BaseStream.Position + bytesNeeded > r.BaseStream.Length) return false;

            r.BaseStream.Seek(bytesNeeded, SeekOrigin.Current);
            return true;
        }

        /// <summary>
        /// 兜底：在缓冲区内寻找 UniqueTextureSpace + BodyType + BodySubType 特征序列。
        /// </summary>
        private static bool TryScanBodyTypeFallback(byte[] data, out int bodyType)
        {
            bodyType = 0;
            // 模式：1 byte + int32 bodyType(1..0x50) + int32 + uint32 ageGender(非零常见)
            for (int i = 0; i < data.Length - 13; i++)
            {
                int bt = BitConverter.ToInt32(data, i + 1);
                if (bt < 1 || bt > 0x50) continue;

                // bodySubType 常见为较小整数
                int sub = BitConverter.ToInt32(data, i + 5);
                if (sub < -1 || sub > 0x1000) continue;

                uint ageGender = BitConverter.ToUInt32(data, i + 9);
                // AgeGender 标志位通常落在低位区间
                if (ageGender == 0 || ageGender > 0x00FFFFFF) continue;

                bodyType = bt;
                return true;
            }

            return false;
        }
    }
}
