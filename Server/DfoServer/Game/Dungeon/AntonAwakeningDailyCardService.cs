using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct AntonAwakeningRewardDefinition
    {
        internal AntonAwakeningRewardDefinition(int itemId, int state)
        {
            ItemId = itemId;
            State = state;
        }

        internal int ItemId { get; }
        internal int State { get; }
        internal bool IsValid => ItemId > 0 && State >= 0;
    }

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
    /// Resolves the final-dungeon reward definition from PVF and owns only the
    /// daily claim policy. Per-clear mutable state belongs to DungeonInstance.
    /// </summary>
    internal sealed class AntonAwakeningDailyCardService
    {
        internal const int FinalDungeonId = 247;
        internal const string RewardCounterKey = "anton_awakening_card_247";
        private const int SequentialConfigKey = 41;

        private readonly DailyResetService _dailyReset;
        private readonly IReadOnlyList<AntonAwakeningRewardCandidate> _candidates;
        private readonly int _totalWeight;
        private readonly Func<int, int> _nextRoll;

        internal AntonAwakeningDailyCardService(DailyResetService dailyReset)
            : this(dailyReset, LoadCandidates(), null)
        {
        }

        internal AntonAwakeningDailyCardService(
            DailyResetService dailyReset,
            IReadOnlyList<AntonAwakeningRewardCandidate> candidates,
            Func<int, int> nextRoll = null)
        {
            _dailyReset = dailyReset;
            _nextRoll = nextRoll ?? ServerRandom.Next;
            _candidates = candidates ?? Array.Empty<AntonAwakeningRewardCandidate>();
            var total = 0L;
            foreach (var candidate in _candidates)
            {
                if (candidate == null
                    || candidate.Weight <= 0
                    || !candidate.Reward.IsValid)
                {
                    total = 0;
                    _candidates = Array.Empty<AntonAwakeningRewardCandidate>();
                    break;
                }
                total += candidate.Weight;
                if (total > int.MaxValue)
                {
                    total = 0;
                    _candidates = Array.Empty<AntonAwakeningRewardCandidate>();
                    break;
                }
            }
            _totalWeight = (int)total;
        }

        internal bool IsConfigured => _candidates.Count > 0 && _totalWeight > 0;

        internal bool TryDrawReward(out AntonAwakeningRewardDefinition reward)
        {
            reward = default;
            if (!IsConfigured)
                return false;

            var roll = _nextRoll(_totalWeight);
            if (roll < 0 || roll >= _totalWeight)
                return false;
            foreach (var candidate in _candidates)
            {
                if (roll < candidate.Weight)
                {
                    reward = candidate.Reward;
                    return true;
                }
                roll -= candidate.Weight;
            }
            return false;
        }

        internal bool HasClaimedRewardToday(int characterId)
            => _dailyReset != null
               && characterId > 0
               && _dailyReset.IsClaimed(characterId, RewardCounterKey);

        internal bool TryClaimReward(int characterId)
            => _dailyReset != null
               && characterId > 0
               && _dailyReset.TryIncrementCounter(
                   characterId,
                   RewardCounterKey,
                   cap: 1,
                   period: DailyResetService.PeriodDay);

        internal bool TryClaimReward(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
            => _dailyReset != null
               && connection != null
               && transaction != null
               && characterId > 0
               && _dailyReset.TryIncrementCounter(
                   connection,
                   transaction,
                   characterId,
                   RewardCounterKey,
                   cap: 1,
                   period: DailyResetService.PeriodDay);

        internal static IReadOnlyList<AntonAwakeningRewardCandidate>
            Parse247ClearRewards(string config)
        {
            if (string.IsNullOrWhiteSpace(config))
                return null;

            var root = new ScriptParser().Parse(config);
            foreach (var section in root.GetChildren("sequential dungeon"))
            {
                var keyTokens = ScriptValueTokenizer.Tokenize(
                    section.GetFirstDataContent(config));
                if (keyTokens.Count != 1
                    || !int.TryParse(keyTokens[0], out var key)
                    || key != SequentialConfigKey)
                {
                    continue;
                }

                var rewardNode = section.GetChild("clear reward item");
                if (rewardNode == null)
                    return null;

                var tokens = ScriptValueTokenizer.Tokenize(
                    rewardNode.GetFirstDataContent(config));
                if (tokens.Count == 0 || tokens.Count % 3 != 0)
                    return null;

                var result = new List<AntonAwakeningRewardCandidate>(
                    tokens.Count / 3);
                var totalWeight = 0L;
                for (var index = 0; index < tokens.Count; index += 3)
                {
                    if (!int.TryParse(tokens[index], out var weight)
                        || !int.TryParse(tokens[index + 1], out var itemId)
                        || !int.TryParse(tokens[index + 2], out var state)
                        || weight <= 0
                        || itemId <= 0
                        || state < 0)
                    {
                        return null;
                    }

                    totalWeight += weight;
                    if (totalWeight > int.MaxValue)
                        return null;
                    result.Add(new AntonAwakeningRewardCandidate(
                        weight,
                        new AntonAwakeningRewardDefinition(itemId, state)));
                }
                return result.AsReadOnly();
            }
            return null;
        }

        private static IReadOnlyList<AntonAwakeningRewardCandidate>
            LoadCandidates()
        {
            try
            {
                var config = PvfArchiveAccessor.ReadText(
                    "etc/sequential_dungeon_info.etc");
                var candidates = Parse247ClearRewards(config)
                    ?? Array.Empty<AntonAwakeningRewardCandidate>();
                FileLogger.Log(
                    "[AntonAwakeningDailyCardService] loaded 247 rewards: "
                    + $"candidates={candidates.Count} "
                    + $"totalWeight={candidates.Sum(value => value.Weight)} "
                    + $"pool=[{string.Join(",", candidates.Select(value =>
                        $"{value.Weight}:{value.Reward.ItemId}:{value.Reward.State}"))}]");
                return candidates;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[AntonAwakeningDailyCardService] failed to load "
                    + $"etc/sequential_dungeon_info.etc: {ex.Message}");
                return Array.Empty<AntonAwakeningRewardCandidate>();
            }
        }
    }
}
