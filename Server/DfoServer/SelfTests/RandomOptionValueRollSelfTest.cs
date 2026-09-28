using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.SelfTests
{
    public static class RandomOptionValueRollSelfTest
    {
        private const int FireAttackOptionId = 159;
        private const int CastSpeedOptionId = 131;
        private const int HpMaxOptionId = 104;
        private const int SampleRollCount = 60;
        private const int EndToEndRollCount = 40;

        public static int Run()
        {
            Console.WriteLine("=== RANDOM_OPTION_VALUE_ROLL selftest ===");
            var failures = 0;

            if (!HasRealPvf())
            {
                Console.WriteLine(
                    "[SKIP] real PVF random option value roll checks: PVF_ARCHIVE_PATH is not set");
            }
            else
            {
                VerifyFireAttackRollsWithinDungeonRange(ref failures);
                VerifyCastSpeedPrefersDungeonRowOverPvpRow(ref failures);
                VerifyOversizedRangeRollsAfterByteClamp(ref failures);
                VerifyTryRollOptionsEndToEnd(ref failures);
            }

            Console.WriteLine(failures == 0
                ? "RANDOM_OPTION_VALUE_ROLL selftest passed"
                : $"RANDOM_OPTION_VALUE_ROLL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyFireAttackRollsWithinDungeonRange(ref int failures)
        {
            // randomoptions_159_fireattack.etc 70级 PvE 行: `2 11`
            var entries = SampleRolls(FireAttackOptionId, 70, SampleRollCount);
            Check(
                "fireattack @70 Value1 在官方区间 [2,11] 内随机且 Value2 恒为行 max 11",
                entries.Count == SampleRollCount
                    && entries.All(e => e.Type == FireAttackOptionId
                        && e.Value1 >= 2
                        && e.Value1 <= 11
                        && e.Value2 == 11)
                    && entries.Select(e => e.Value1).Distinct().Count() > 1,
                ref failures);
        }

        private static void VerifyCastSpeedPrefersDungeonRowOverPvpRow(ref int failures)
        {
            // randomoptions_131_castspeed.etc 带 [pvp] 段; 70级 PvE 行 `13 77`, PvP 行 `7 39`
            var entries = SampleRolls(CastSpeedOptionId, 70, SampleRollCount);
            Check(
                "castspeed @70 取 PvE 段等级行: Value2 == 77(而非 PvP 的 39) 且 Value1 在 [13,77]",
                entries.Count == SampleRollCount
                    && entries.All(e => e.Type == CastSpeedOptionId
                        && e.Value1 >= 13
                        && e.Value1 <= 77
                        && e.Value2 == 77)
                    && entries.Select(e => e.Value1).Distinct().Count() > 1,
                ref failures);
        }

        private static void VerifyOversizedRangeRollsAfterByteClamp(ref int failures)
        {
            // randomoptions_104_hpmax.etc 85级行 `109 480` 超字节域: 在截断后区间 [109,255] 内 roll
            var entries = SampleRolls(HpMaxOptionId, 85, SampleRollCount);
            Check(
                "hpmax @85 超字节域行截断后 roll: Value1 在 [109,255] 且 Value2 == 255, 不越界不抛异常",
                entries.Count == SampleRollCount
                    && entries.All(e => e.Type == HpMaxOptionId
                        && e.Value1 >= 109
                        && e.Value1 <= 255
                        && e.Value2 == 255)
                    && entries.Select(e => e.Value1).Distinct().Count() > 1,
                ref failures);
        }

        private static void VerifyTryRollOptionsEndToEnd(ref int failures)
        {
            var metadata = new ItemMetadata
            {
                ItemKind = "equipment",
                EquipmentType = "coat",
                Rarity = 2,
                MinimumLevel = 70,
                PvfFilePath = "equipment/cloth/sealroll_selftest.coat",
            };

            var allSucceeded = true;
            var allInOfficialRange = true;
            var slotZeroValuesByType = new Dictionary<int, List<int>>();
            var officialRanges = new Dictionary<int, (int min, int max)>();

            for (var i = 0; i < EndToEndRollCount; i++)
            {
                if (!RandomOptionResolver.TryRollOptions(metadata, out var entries)
                    || entries == null
                    || entries.Count == 0)
                {
                    allSucceeded = false;
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (!officialRanges.TryGetValue(entry.Type, out var range))
                    {
                        if (!TryResolveOfficialDungeonRange(entry.Type, metadata.MinimumLevel, out range))
                        {
                            allInOfficialRange = false;
                            continue;
                        }

                        officialRanges[entry.Type] = range;
                    }

                    var maxEff = Math.Min(range.max, 255);
                    var minEff = Math.Min(range.min, maxEff);
                    if (entry.Value1 < minEff
                        || entry.Value1 > maxEff
                        || entry.Value2 != ClampByte(range.max))
                    {
                        allInOfficialRange = false;
                    }
                }

                var first = entries[0];
                if (!slotZeroValuesByType.TryGetValue(first.Type, out var values))
                {
                    values = new List<int>();
                    slotZeroValuesByType[first.Type] = values;
                }

                values.Add(first.Value1);
            }

            Check(
                "TryRollOptions 端到端多次 roll 全部成功且数值落在官方区间(独立重解析 PVF 校验)",
                allSucceeded && allInOfficialRange && officialRanges.Count > 0,
                ref failures);

            Check(
                "TryRollOptions 同槽位同属性数值随 roll 变化",
                slotZeroValuesByType.Any(pair => pair.Value.Distinct().Count() > 1),
                ref failures);
        }

        private static List<RandomOptionEntry> SampleRolls(int optionId, int itemLevel, int count)
        {
            var entries = new List<RandomOptionEntry>(count);
            for (var i = 0; i < count; i++)
                entries.Add(RandomOptionResolver.RollOptionValue(optionId, itemLevel));
            return entries;
        }

        // 测试侧独立从 PVF 原文重解析 (optionId, itemLevel) 的官方 PvE 区间, 作为被测实现的对照。
        private static bool TryResolveOfficialDungeonRange(
            int optionId,
            int itemLevel,
            out (int min, int max) range)
        {
            range = (1, 1);

            var listText = PvfArchiveAccessor.ReadText("etc/randomoption/randomoption.lst");
            var pathMatch = Regex.Match(
                listText,
                @"(?:^|\s)" + optionId + @"\s+`([^`]+)`");
            if (!pathMatch.Success)
                return false;

            var relativePath = pathMatch.Groups[1].Value.Replace('\\', '/');
            var text = PvfArchiveAccessor.ReadText("etc/randomoption/" + relativePath);

            var source = text ?? string.Empty;
            var dungeonMatch = Regex.Match(
                source,
                @"\[dungeon\](.*?)\[/dungeon\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (dungeonMatch.Success)
                source = dungeonMatch.Groups[1].Value;

            var bestLevel = -1;
            var found = false;
            foreach (Match match in Regex.Matches(
                source,
                @"\[level\]\s*(.*?)\[/level\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var ints = Regex.Matches(match.Groups[1].Value, @"-?\d+")
                    .Cast<Match>()
                    .Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture))
                    .ToList();
                if (ints.Count < 3)
                    continue;

                var level = ints[0];
                if (level > itemLevel || level < bestLevel)
                    continue;

                bestLevel = level;
                range = (ints[ints.Count - 2], ints[ints.Count - 1]);
                found = true;
            }

            return found;
        }

        private static bool HasRealPvf()
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            return !string.IsNullOrWhiteSpace(pvfPath) && File.Exists(pvfPath);
        }

        private static byte ClampByte(int value)
        {
            if (value < 0)
                return 0;
            if (value > 255)
                return 255;
            return (byte)value;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
