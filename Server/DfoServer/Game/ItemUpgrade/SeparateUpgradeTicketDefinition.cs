using DfoServer.Game.Inventory;
using PvfLib;
using System;

namespace DfoServer.Game.ItemUpgrade
{
    internal sealed class SeparateUpgradeTicketDefinition
    {
        // 幸运锻造券：[action type] `[lucky enchant deed]` 2。
        // PVF 里这类券不写 [equipment separate reinforcement ticket]，
        // 行为固定为「锻造阶段 +1，不失败」，参数 2 表示锻造。
        internal const string LuckyEnchantDeedActionType = "[lucky enchant deed]";
        internal const int LuckyEnchantDeedRefineParam = 2;
        private const int LuckyRefineTargetLevel = 1;
        private const int LuckyRefineSuccessWeight = 10000;

        internal int ItemTemplateId { get; private set; }
        internal byte TargetLevel { get; private set; }
        internal int SuccessWeight { get; private set; }
        internal bool IsFixed { get; private set; }
        internal bool IsAdditional { get; private set; }
        internal int ApplyValue { get; private set; }
        internal bool IsLuckyRefineTicket { get; private set; }

        internal static bool TryLoad(int itemTemplateId, out SeparateUpgradeTicketDefinition definition)
        {
            definition = null;
            var stackable = StackableItemProvider.Load(itemTemplateId);
            return TryParse(itemTemplateId, stackable, out definition);
        }

        internal static bool TryParse(
            int itemTemplateId,
            StackableItemFile stackable,
            out SeparateUpgradeTicketDefinition definition)
        {
            definition = null;
            var source = stackable?.EquipmentSeparateReinforcementTicket;
            if (source == null)
                return TryParseLuckyRefineTicket(itemTemplateId, stackable, out definition);
            var isFixed = string.Equals(source.ApplyMode, "fixed", StringComparison.OrdinalIgnoreCase);
            var isAdditional = string.Equals(source.ApplyMode, "additional", StringComparison.OrdinalIgnoreCase);
            if (source.TargetLevel <= 0 || source.TargetLevel > byte.MaxValue
                || source.SuccessRatePercent < 0 || source.SuccessRatePercent > 100
                || (!isFixed && !isAdditional))
            {
                return false;
            }

            definition = new SeparateUpgradeTicketDefinition
            {
                ItemTemplateId = itemTemplateId,
                TargetLevel = checked((byte)source.TargetLevel),
                SuccessWeight = source.SuccessRatePercent * 100,
                IsFixed = isFixed,
                IsAdditional = isAdditional,
                ApplyValue = source.ApplyValue,
            };
            return true;
        }

        private static bool TryParseLuckyRefineTicket(
            int itemTemplateId,
            StackableItemFile stackable,
            out SeparateUpgradeTicketDefinition definition)
        {
            definition = null;
            if (stackable == null)
                return false;

            var action = stackable.ActionTypeName;
            if (string.IsNullOrWhiteSpace(action))
                return false;

            if (!string.Equals(
                    action.Trim().Trim('`').Trim(),
                    LuckyEnchantDeedActionType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (stackable.ActionTypeParams.Count == 0
                || stackable.ActionTypeParams[0] != LuckyEnchantDeedRefineParam)
            {
                return false;
            }

            definition = new SeparateUpgradeTicketDefinition
            {
                ItemTemplateId = itemTemplateId,
                TargetLevel = LuckyRefineTargetLevel,
                SuccessWeight = LuckyRefineSuccessWeight,
                IsFixed = false,
                IsAdditional = true,
                ApplyValue = -1,
                IsLuckyRefineTicket = true,
            };
            return true;
        }
    }
}
