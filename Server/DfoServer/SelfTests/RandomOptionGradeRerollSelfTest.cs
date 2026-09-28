using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests
{
    public static class RandomOptionGradeRerollSelfTest
    {
        private const int KaleidoBoxItemId = 15;
        // character/common/belt/cloth/100200003.equ: 稀有度 2(稀有) 最低等级 65 布甲腰带(可调整品级的装备类型)
        private const int RareWaistItemId = 100200003;
        private const int FireAttackOptionId = 159;
        private const int PhysicalAttackOptionId = 70;
        private const int UnableToModifyOptionId = 180;
        private const int GradelessOptionId = 131;
        private const int DistributionRollCount = 200;

        public static int Run()
        {
            Console.WriteLine("=== RANDOM_OPTION_GRADE_REROLL selftest ===");
            var failures = 0;

            if (!HasRealPvf())
            {
                Console.WriteLine(
                    "[SKIP] real PVF random option grade reroll checks: PVF_ARCHIVE_PATH is not set");
            }
            else
            {
                VerifyResetItemQualityRerollsSealValues(ref failures);
                VerifySkipListsKeepOriginalValues(ref failures);
                VerifyWeightedBandDistribution(ref failures);
                VerifyResetWithoutSealOptionsKeepsLegacyBehavior(ref failures);
            }

            Console.WriteLine(failures == 0
                ? "RANDOM_OPTION_GRADE_REROLL selftest passed"
                : $"RANDOM_OPTION_GRADE_REROLL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyResetItemQualityRerollsSealValues(ref int failures)
        {
            var inventory = new InventoryService(92001, 92002);
            inventory.SetItem(
                InventoryListType.Main,
                10,
                CreateEquipmentCore(
                    new RandomOption { Type = FireAttackOptionId, Value1 = 5, Value2 = 11 },
                    new RandomOption { Type = PhysicalAttackOptionId, Value1 = 30, Value2 = 61 }));
            inventory.SetItem(InventoryListType.Main, 20, CreateKaleidoBoxCore(2));

            var ok = InventoryEquipmentMutationService.TryResetItemQuality(
                inventory,
                new ResetItemQualityRequest
                {
                    TargetSlotIndex = 10,
                    TargetItemTemplateId = RareWaistItemId,
                    MaterialSlotIndex = 20,
                },
                out var result);

            var updated = inventory.GetItem(InventoryListType.Main, 10);
            var options = updated != null ? updated.RandomOptions : null;
            // 65级 PvE 行: 159_fireattack `2 11`, 70_physicalattack `9 61`
            Check(
                "品级调整箱成功后装备品级种子重 roll、材料消耗 1 个、封印条数/类型/顺序不变",
                ok
                    && result != null
                    && result.ErrorCode == 0
                    && result.OldQualitySeed != result.NewQualitySeed
                    && result.MaterialRemainingCount == 1
                    && inventory.GetItem(InventoryListType.Main, 20)?.Count == 1
                    && updated != null
                    && updated.Value == result.NewQualitySeed
                    && options != null
                    && options.Count == 2
                    && options[0].Type == FireAttackOptionId
                    && options[1].Type == PhysicalAttackOptionId,
                ref failures);

            Check(
                "封印数值重 roll 后落在官方区间且 Value2 恒为行 max",
                options != null
                    && options.Count == 2
                    && options[0].Value1 >= 2
                    && options[0].Value1 <= 11
                    && options[0].Value2 == 11
                    && options[1].Value1 >= 9
                    && options[1].Value1 <= 61
                    && options[1].Value2 == 61,
                ref failures);
        }

        private static void VerifySkipListsKeepOriginalValues(ref int failures)
        {
            var inventory = new InventoryService(92011, 92012);
            inventory.SetItem(
                InventoryListType.Main,
                10,
                CreateEquipmentCore(
                    new RandomOption { Type = UnableToModifyOptionId, Value1 = 3, Value2 = 3 },
                    new RandomOption { Type = GradelessOptionId, Value1 = 20, Value2 = 39 },
                    new RandomOption { Type = PhysicalAttackOptionId, Value1 = 1, Value2 = 61 }));
            inventory.SetItem(InventoryListType.Main, 20, CreateKaleidoBoxCore(1));

            var ok = InventoryEquipmentMutationService.TryResetItemQuality(
                inventory,
                new ResetItemQualityRequest
                {
                    TargetSlotIndex = 10,
                    TargetItemTemplateId = RareWaistItemId,
                    MaterialSlotIndex = 20,
                },
                out var result);

            var options = inventory.GetItem(InventoryListType.Main, 10)?.RandomOptions;
            Check(
                "unable/gradeless 名单内属性保持原值, 名单外属性正常重 roll",
                ok
                    && result != null
                    && result.ErrorCode == 0
                    && options != null
                    && options.Count == 3
                    && options[0].Type == UnableToModifyOptionId
                    && options[0].Value1 == 3
                    && options[0].Value2 == 3
                    && options[1].Type == GradelessOptionId
                    && options[1].Value1 == 20
                    && options[1].Value2 == 39
                    && options[2].Type == PhysicalAttackOptionId
                    && options[2].Value1 >= 9
                    && options[2].Value1 <= 61
                    && options[2].Value2 == 61,
                ref failures);
        }

        private static void VerifyWeightedBandDistribution(ref int failures)
        {
            var metadata = ItemMetadataResolver.Resolve(RareWaistItemId);
            if (!Check(
                    "模板元数据为稀有度 2 / 最低等级 65",
                    metadata != null && metadata.Rarity == 2 && metadata.MinimumLevel == 65,
                    ref failures))
            {
                return;
            }

            // 稀有(2)档位: [0,14] w330 / [14,37] w520 / [37,47] w100 / [47,56] w50。
            // 159 @65 官方区间 [2,11]: 低档(p<=14) 只产出 v1<=3, 高档(p>=47) 只产出 v1>=6。
            var values = new List<int>(DistributionRollCount);
            var allRerolled = true;
            for (var i = 0; i < DistributionRollCount; i++)
            {
                if (!RandomOptionResolver.TryRerollOptionValueForGradeChange(
                        metadata,
                        FireAttackOptionId,
                        out var entry)
                    || entry == null)
                {
                    allRerolled = false;
                    continue;
                }

                values.Add(entry.Value1);
            }

            var lowBandCount = values.Count(v => v <= 3);
            var highBandCount = values.Count(v => v >= 6);
            Check(
                $"重复调整 {DistributionRollCount} 次全部成功, 数值不全相同, 高档位(47~56 档)频率显著低于低档位(0~14 档)",
                allRerolled
                    && values.Count == DistributionRollCount
                    && values.All(v => v >= 2 && v <= 11)
                    && values.Distinct().Count() > 1
                    && highBandCount < lowBandCount,
                ref failures);
        }

        private static void VerifyResetWithoutSealOptionsKeepsLegacyBehavior(ref int failures)
        {
            var inventory = new InventoryService(92021, 92022);
            inventory.SetItem(InventoryListType.Main, 10, CreateEquipmentCore());
            inventory.SetItem(InventoryListType.Main, 20, CreateKaleidoBoxCore(1));

            var ok = InventoryEquipmentMutationService.TryResetItemQuality(
                inventory,
                new ResetItemQualityRequest
                {
                    TargetSlotIndex = 10,
                    TargetItemTemplateId = RareWaistItemId,
                    MaterialSlotIndex = 20,
                },
                out var result);

            var updated = inventory.GetItem(InventoryListType.Main, 10);
            var material = inventory.GetItem(InventoryListType.Main, 20);
            Check(
                "无封印属性装备走原路径: 品级种子重 roll、无封印属性产生、材料正常消耗",
                ok
                    && result != null
                    && result.ErrorCode == 0
                    && result.OldQualitySeed != result.NewQualitySeed
                    && result.MaterialRemainingCount == 0
                    && updated != null
                    && updated.Value == result.NewQualitySeed
                    && updated.RandomOptions.Count == 0
                    && (material == null || material.Count == 0),
                ref failures);
        }

        private static ItemCore CreateEquipmentCore(params RandomOption[] options)
        {
            var core = new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = RareWaistItemId,
                Value = 1,
            };
            if (options != null && options.Length > 0)
                core.SetRandomOptions(options);
            return core;
        }

        private static ItemCore CreateKaleidoBoxCore(int count)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindConsumable,
                ItemId = KaleidoBoxItemId,
                Count = count,
            };
        }

        private static bool HasRealPvf()
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            return !string.IsNullOrWhiteSpace(pvfPath) && File.Exists(pvfPath);
        }

        private static bool Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
            return condition;
        }
    }
}
