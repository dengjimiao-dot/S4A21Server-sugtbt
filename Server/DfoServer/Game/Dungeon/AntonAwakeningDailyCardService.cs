using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    // Immutable value frozen into an instance reward plan. The outer reward
    // group is retained for diagnostics; ItemId/Quantity are always final.
    internal readonly struct AntonAwakeningRewardDefinition
    {
        internal AntonAwakeningRewardDefinition(
            int groupKey,
            int rewardableDungeonId,
            int rewardGroupItemId,
            int itemId,
            int quantity,
            int cardState)
        {
            GroupKey = groupKey;
            RewardableDungeonId = rewardableDungeonId;
            RewardGroupItemId = rewardGroupItemId;
            ItemId = itemId;
            Quantity = quantity;
            CardState = cardState;
        }

        // Compatibility only for pre-Task3 deterministic self-tests. The
        // production path uses the Definition/STK overload below.
        internal AntonAwakeningRewardDefinition(int itemId, int state)
            : this(1, 1, itemId, itemId, 1, state)
        {
        }

        internal int GroupKey { get; }
        internal int RewardableDungeonId { get; }
        internal int RewardGroupItemId { get; }
        internal int ItemId { get; }
        internal int Quantity { get; }
        internal int CardState { get; }

        internal int State => CardState;

        internal bool IsValid => GroupKey > 0
            && RewardableDungeonId > 0
            && RewardGroupItemId > 0
            && ItemId > 0
            && Quantity > 0
            && CardState >= 0;
    }

    // Legacy injected candidate retained for old deterministic self-tests.
    // Server composition uses the stackable-loader constructor instead.
    internal sealed class AntonAwakeningRewardCandidate
    {
        internal AntonAwakeningRewardCandidate(
            int weight,
            AntonAwakeningRewardDefinition reward)
        {
            Weight = weight;
            Reward = reward;
        }

        internal int Weight { get; }
        internal AntonAwakeningRewardDefinition Reward { get; }
    }

    /// <summary>
    /// Resolves special rewards from the immutable sequential ETC definition
    /// and the existing PvfLib stackable parser. It also owns the dynamic
    /// daily claim key, while durable state remains in DailyResetService.
    /// </summary>
    internal sealed class AntonAwakeningDailyCardService
    {
        private const string RewardPath = "etc/sequential_dungeon_info.etc";
        private const int LegacyGroupKey = 1;
        private const int LegacyRewardableDungeonId = 1;

        private readonly DailyResetService _dailyReset;
        private readonly Func<int, StackableItemFile> _stackableLoader;
        private readonly Func<int, int> _nextRoll;
        private readonly IReadOnlyList<AntonAwakeningRewardCandidate>
            _legacyCandidates;
        private readonly int _legacyTotalWeight;

        internal AntonAwakeningDailyCardService(DailyResetService dailyReset)
            : this(dailyReset, StackableItemProvider.Load, null)
        {
        }

        internal AntonAwakeningDailyCardService(
            DailyResetService dailyReset,
            Func<int, StackableItemFile> stackableLoader,
            Func<int, int> nextRoll = null)
        {
            _dailyReset = dailyReset;
            _stackableLoader = stackableLoader ?? StackableItemProvider.Load;
            _nextRoll = nextRoll ?? ServerRandom.Next;
            _legacyCandidates = Array.Empty<AntonAwakeningRewardCandidate>();
            _legacyTotalWeight = 0;
        }

        // Compatibility overload for existing deterministic self-tests.
        internal AntonAwakeningDailyCardService(
            DailyResetService dailyReset,
            IReadOnlyList<AntonAwakeningRewardCandidate> candidates,
            Func<int, int> nextRoll = null)
        {
            _dailyReset = dailyReset;
            _stackableLoader = StackableItemProvider.Load;
            _nextRoll = nextRoll ?? ServerRandom.Next;
            _legacyCandidates = candidates ??
                Array.Empty<AntonAwakeningRewardCandidate>();

            var total = 0L;
            foreach (var candidate in _legacyCandidates)
            {
                if (candidate == null
                    || candidate.Weight <= 0
                    || !candidate.Reward.IsValid)
                {
                    total = 0;
                    _legacyCandidates =
                        Array.Empty<AntonAwakeningRewardCandidate>();
                    break;
                }

                total += candidate.Weight;
                if (total > int.MaxValue)
                {
                    total = 0;
                    _legacyCandidates =
                        Array.Empty<AntonAwakeningRewardCandidate>();
                    break;
                }
            }
            _legacyTotalWeight = (int)total;
        }

        internal bool IsConfigured => _legacyTotalWeight > 0
            || _stackableLoader != null;

        // Two-stage draw: clear reward item -> STK upgradable legacy entry.
        internal bool TryDrawReward(
            SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            out AntonAwakeningRewardDefinition reward)
        {
            reward = default;
            // Keep old deterministic self-tests isolated from production
            // resolution. The server composition never supplies candidates;
            // when it does, the injected result is already a frozen final
            // reward and no PVF group id is reinterpreted here.
            if (_legacyTotalWeight > 0)
                return TryDrawReward(out reward);

            if (definition == null
                || rewardableDungeonId <= 0
                || !definition.RewardableDungeonIds.Contains(
                    rewardableDungeonId))
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    0,
                    0,
                    "definition missing or dungeon is not rewardable");
                return false;
            }

            var outer = definition.ClearRewardGroups;
            if (outer == null || outer.Count == 0)
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    0,
                    0,
                    "clear reward group is empty");
                return false;
            }

            if (!TrySelectOuter(
                    outer,
                    out var selectedGroup,
                    out _))
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    0,
                    0,
                    "clear reward group contains invalid weight or roll");
                return false;
            }

            StackableItemFile stackable;
            try
            {
                stackable = _stackableLoader?.Invoke(
                    selectedGroup.RewardGroupItemId);
            }
            catch (Exception ex)
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    0,
                    "stackable loader threw: " + ex.Message,
                    selectedGroup.CardState);
                return false;
            }

            if (stackable == null)
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    0,
                    "stackable item is missing",
                    selectedGroup.CardState);
                return false;
            }

            var stackableType = StackableItemProvider.NormalizeType(
                stackable.StackableType);
            if (!string.Equals(
                    stackableType,
                    StackableItemProvider.UpgradableLegacyType,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    stackableType,
                    StackableItemProvider.RandomUpgradableLegacyType,
                    StringComparison.OrdinalIgnoreCase))
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    0,
                    "stackable type is not upgradable legacy",
                    selectedGroup.CardState);
                return false;
            }

            var inner = stackable.UpgradableLegacyRewards;
            if (inner == null || inner.Count == 0)
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    0,
                    "upgradable legacy rewards are empty",
                    selectedGroup.CardState);
                return false;
            }

            var totalInnerWeight = 0L;
            foreach (var candidate in inner)
            {
                if (candidate == null
                    || candidate.ItemId <= 0
                    || candidate.Weight <= 0
                    || candidate.Count <= 0)
                {
                    LogFailure(
                        definition,
                        rewardableDungeonId,
                        selectedGroup.RewardGroupItemId,
                        candidate?.ItemId ?? 0,
                        "upgradable legacy entry is invalid",
                        selectedGroup.CardState);
                    return false;
                }

                totalInnerWeight += candidate.Weight;
                if (totalInnerWeight > int.MaxValue)
                {
                    LogFailure(
                        definition,
                        rewardableDungeonId,
                        selectedGroup.RewardGroupItemId,
                        candidate.ItemId,
                        "upgradable legacy weight exceeds Int32 capacity",
                        selectedGroup.CardState);
                    return false;
                }
            }

            if (!TryRoll((int)totalInnerWeight, out var innerRoll))
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    0,
                    "upgradable legacy roll is invalid",
                    selectedGroup.CardState);
                return false;
            }

            PvfLib.BoosterRewardEntry selectedInner = null;
            var remaining = innerRoll;
            foreach (var candidate in inner)
            {
                if (remaining < candidate.Weight)
                {
                    selectedInner = candidate;
                    break;
                }
                remaining -= candidate.Weight;
            }

            if (selectedInner == null)
            {
                LogFailure(
                    definition,
                    rewardableDungeonId,
                    selectedGroup.RewardGroupItemId,
                    0,
                    "upgradable legacy roll did not select an entry",
                    selectedGroup.CardState);
                return false;
            }

            reward = new AntonAwakeningRewardDefinition(
                definition.GroupKey,
                rewardableDungeonId,
                selectedGroup.RewardGroupItemId,
                selectedInner.ItemId,
                selectedInner.Count,
                selectedGroup.CardState);
            return reward.IsValid;
        }

        // Compatibility draw for pre-Task3 tests.
        internal bool TryDrawReward(out AntonAwakeningRewardDefinition reward)
        {
            reward = default;
            if (_legacyTotalWeight <= 0
                || !TryRoll(_legacyTotalWeight, out var roll))
            {
                return false;
            }

            foreach (var candidate in _legacyCandidates)
            {
                if (roll < candidate.Weight)
                {
                    reward = candidate.Reward;
                    return reward.IsValid;
                }
                roll -= candidate.Weight;
            }
            return false;
        }

        internal bool HasClaimedRewardToday(
            int characterId,
            int groupKey,
            int rewardableDungeonId)
        {
            var key = BuildRewardCounterKey(groupKey, rewardableDungeonId);
            return _dailyReset != null
                && characterId > 0
                && key.Length > 0
                && _dailyReset.IsClaimed(characterId, key);
        }

        internal bool HasClaimedRewardToday(int characterId)
            => HasClaimedRewardToday(
                characterId,
                LegacyGroupKey,
                LegacyRewardableDungeonId);

        internal bool TryClaimReward(
            int characterId,
            int groupKey,
            int rewardableDungeonId)
            => _dailyReset != null
                && characterId > 0
                && _dailyReset.TryIncrementCounter(
                    characterId,
                    BuildRewardCounterKey(groupKey, rewardableDungeonId),
                    cap: 1,
                    period: DailyResetService.PeriodDay);

        internal bool TryClaimReward(int characterId)
            => TryClaimReward(
                characterId,
                LegacyGroupKey,
                LegacyRewardableDungeonId);

        internal bool TryClaimReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupKey,
            int rewardableDungeonId)
            => _dailyReset != null
                && connection != null
                && transaction != null
                && characterId > 0
                && _dailyReset.TryIncrementCounter(
                    connection,
                    transaction,
                    characterId,
                    BuildRewardCounterKey(groupKey, rewardableDungeonId),
                    cap: 1,
                    period: DailyResetService.PeriodDay);

        internal bool TryClaimReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
            => TryClaimReward(
                connection,
                transaction,
                characterId,
                LegacyGroupKey,
                LegacyRewardableDungeonId);

        internal static string BuildRewardCounterKey(
            int groupKey,
            int rewardableDungeonId)
            => groupKey > 0 && rewardableDungeonId > 0
                ? "sequential_reward_v1:"
                    + groupKey
                    + ":"
                    + rewardableDungeonId
                : string.Empty;

        private bool TrySelectOuter(
            IReadOnlyList<SequentialDungeonRewardGroup> groups,
            out SequentialDungeonRewardGroup selected,
            out int roll)
        {
            selected = null;
            roll = 0;
            var total = 0L;
            foreach (var group in groups)
            {
                if (group == null
                    || group.Weight <= 0
                    || group.RewardGroupItemId <= 0
                    || group.CardState < 0)
                {
                    return false;
                }
                total += group.Weight;
                if (total > int.MaxValue)
                    return false;
            }

            if (total <= 0 || !TryRoll((int)total, out roll))
                return false;

            var remaining = roll;
            foreach (var group in groups)
            {
                if (remaining < group.Weight)
                {
                    selected = group;
                    break;
                }
                remaining -= group.Weight;
            }
            return selected != null;
        }

        private bool TryRoll(int maximum, out int roll)
        {
            roll = 0;
            if (maximum <= 0)
                return false;
            try
            {
                roll = _nextRoll(maximum);
                return roll >= 0 && roll < maximum;
            }
            catch
            {
                return false;
            }
        }

        private static void LogFailure(
            SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            int rewardGroupItemId,
            int finalItemId,
            string reason,
            int cardState = 0)
        {
            FileLogger.Log(
                "[AntonAwakeningDailyCardService] reward resolution failed: "
                + $"path={RewardPath} "
                + $"group={definition?.GroupKey ?? 0} "
                + $"rewardableDungeon={rewardableDungeonId} "
                + $"rewardGroup={rewardGroupItemId} "
                + $"card={cardState} "
                + $"finalItem={finalItemId} "
                + $"reason={reason}");
        }
    }
}
