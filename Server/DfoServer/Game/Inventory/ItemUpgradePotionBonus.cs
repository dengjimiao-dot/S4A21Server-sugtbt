using System;
using System.Globalization;
using DfoServer.Game.ItemUpgrade;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    internal static class ItemUpgradePotionBonus
    {
        private const int WeightScale = 100000;
        private const string ReinforceType = "[item upgrad buff usual upgrade]";
        private const string AmplifyType = "[item upgrad buff amplify upgrade]";

        internal static int ResolveActiveAddWeight(
            InventoryService inventory,
            ItemUpgradeMode mode,
            ItemUpgradeScene scene,
            long nowUnixSeconds)
        {
            if (inventory == null
                || (scene != ItemUpgradeScene.Npc && scene != ItemUpgradeScene.Portable))
                return 0;

            var secretPotionWeight = 0;
            var crystalWeight = 0;
            foreach (var state in inventory.ItemStates.BuildActiveSnapshots(ItemStateKinds.Effect, nowUnixSeconds))
            {
                var definition = StackableItemProvider.Load(state.ItemId);
                if (!TryGetBonus(state.ItemId, definition, mode, out var weight, out var crystal))
                    continue;

                // Variants of the same secret potion do not multiply the bonus.
                if (crystal)
                    crystalWeight = Math.Max(crystalWeight, weight);
                else
                    secretPotionWeight = Math.Max(secretPotionWeight, weight);
            }

            return (int)Math.Min(WeightScale, (long)secretPotionWeight + crystalWeight);
        }

        internal static bool TryGetBonus(
            int itemId,
            StackableItemFile definition,
            ItemUpgradeMode mode,
            out int weight,
            out bool crystal)
        {
            weight = 0;
            crystal = false;
            if (definition == null
                || !definition.HasEffectMaintenance
                || definition.StatChangeDurationMilliseconds <= 0)
                return false;

            var type = StackableItemProvider.NormalizeType(definition.StackableType);
            if (string.Equals(type, "[expert town potion]", StringComparison.OrdinalIgnoreCase)
                && mode == ItemUpgradeMode.Reinforce)
            {
                // These legacy definitions contain no numeric probability field.
                // Local rules: secret potion +1 percentage point; crystal +10 points.
                switch (itemId)
                {
                    case 2600025:
                    case 2749959:
                        weight = 1000;
                        return true;
                    case 10007343:
                        weight = 10000;
                        crystal = true;
                        return true;
                }
            }

            var expectedType = mode == ItemUpgradeMode.Reinforce ? ReinforceType
                : mode == ItemUpgradeMode.Amplify ? AmplifyType : string.Empty;
            if (expectedType.Length == 0
                || !string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase))
                return false;

            // PVF: `[item upgrad buff ... upgrade]` 1000 (scale 100000).
            var raw = definition.StackableType ?? string.Empty;
            var closingBracket = raw.LastIndexOf(']');
            if (closingBracket < 0)
                return false;
            var amount = raw.Substring(closingBracket + 1).Trim().Trim('`').Trim();
            return int.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out weight)
                && weight > 0 && weight <= WeightScale;
        }
    }
}
