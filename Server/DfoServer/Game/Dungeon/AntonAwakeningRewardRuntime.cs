using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;

namespace DfoServer.Game.Dungeon
{
    internal sealed class AntonAwakeningRewardPlanEntry
    {
        internal AntonAwakeningRewardPlanEntry(
            DungeonParticipantRosterEntry participant,
            AntonAwakeningRewardDefinition reward)
        {
            Participant = participant
                ?? throw new ArgumentNullException(nameof(participant));
            if (!reward.IsValid)
                throw new ArgumentException("A valid reward is required.", nameof(reward));
            Reward = reward;
        }

        internal DungeonParticipantRosterEntry Participant { get; }
        internal AntonAwakeningRewardDefinition Reward { get; }
    }

    internal sealed class AntonAwakeningRewardPlan
    {
        internal AntonAwakeningRewardPlan(
            Guid sourceEventId,
            IReadOnlyList<AntonAwakeningRewardPlanEntry> entries)
        {
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException("A source event is required.", nameof(sourceEventId));
            SourceEventId = sourceEventId;
            Entries = entries ?? Array.Empty<AntonAwakeningRewardPlanEntry>();
        }

        internal Guid SourceEventId { get; }
        internal IReadOnlyList<AntonAwakeningRewardPlanEntry> Entries { get; }
    }

    // Instance-owned, in-process plan/result state. It deliberately contains no
    // sessions or sockets; live ownership is resolved when an effect executes.
    internal sealed class AntonAwakeningRewardRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, AntonAwakeningRewardPlan> _plans =
            new Dictionary<Guid, AntonAwakeningRewardPlan>();
        private readonly Dictionary<(
            Guid SourceEventId,
            DungeonParticipantRunIdentity Participant),
            AntonAwakeningRewardGrantResult> _committed =
                new Dictionary<(
                    Guid,
                    DungeonParticipantRunIdentity),
                    AntonAwakeningRewardGrantResult>();
        private readonly Dictionary<(
            Guid SourceEventId,
            DungeonParticipantRunIdentity Participant),
            DateTime> _projectionDeadlinesUtc =
                new Dictionary<(
                    Guid,
                    DungeonParticipantRunIdentity),
                    DateTime>();

        internal bool TryGetOrCreatePlan(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            AntonAwakeningDailyCardService rewards,
            SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            out AntonAwakeningRewardPlan plan)
        {
            lock (_syncRoot)
            {
                if (_plans.TryGetValue(sourceEventId, out plan))
                    return true;
                if (sourceEventId == Guid.Empty
                    || roster == null
                    || roster.Count == 0
                    || rewards == null
                    || !rewards.IsConfigured)
                {
                    plan = null;
                    return false;
                }

                var entries = new List<AntonAwakeningRewardPlanEntry>();
                var participants = new HashSet<DungeonParticipantRunIdentity>();
                var userIds = new HashSet<ushort>();
                foreach (var participant in roster
                             .Where(value => value != null)
                             .OrderBy(value => value.PartySlot)
                             .ThenBy(value => value.ParticipantUserId)
                             .ThenBy(value => value.CharacterId))
                {
                    AntonAwakeningRewardDefinition reward;
                    var drawn = definition != null
                        ? rewards.TryDrawReward(
                            definition,
                            rewardableDungeonId,
                            out reward)
                        : rewards.TryDrawReward(out reward);
                    if (!participants.Add(
                            participant.RunIdentity.ParticipantIdentity)
                        || !userIds.Add(participant.ParticipantUserId)
                        || !drawn)
                    {
                        plan = null;
                        return false;
                    }
                    entries.Add(new AntonAwakeningRewardPlanEntry(
                        participant,
                        reward));
                }

                if (entries.Count == 0)
                {
                    plan = null;
                    return false;
                }

                plan = new AntonAwakeningRewardPlan(
                    sourceEventId,
                    entries.AsReadOnly());
                _plans.Add(sourceEventId, plan);
                return true;
            }
        }

        internal bool TryGetPlan(
            Guid sourceEventId,
            out AntonAwakeningRewardPlan plan)
        {
            lock (_syncRoot)
                return _plans.TryGetValue(sourceEventId, out plan);
        }

        internal bool TryRecordProjectionDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            DateTime deadlineUtc)
        {
            deadlineUtc = NormalizeUtc(deadlineUtc);
            if (sourceEventId == Guid.Empty
                || !participant.IsValid
                || deadlineUtc == DateTime.MinValue)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_plans.TryGetValue(sourceEventId, out var plan)
                    || !plan.Entries.Any(value =>
                        value.Participant.RunIdentity.ParticipantIdentity
                            .Equals(participant)))
                {
                    return false;
                }

                var key = (sourceEventId, participant);
                if (_projectionDeadlinesUtc.TryGetValue(key, out var existing))
                    return existing == deadlineUtc;
                _projectionDeadlinesUtc.Add(key, deadlineUtc);
                return true;
            }
        }

        internal bool TryGetProjectionDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out DateTime deadlineUtc)
        {
            lock (_syncRoot)
            {
                return _projectionDeadlinesUtc.TryGetValue(
                    (sourceEventId, participant),
                    out deadlineUtc);
            }
        }

        internal bool TryRecordCommitted(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            AntonAwakeningRewardGrantResult result)
        {
            if (sourceEventId == Guid.Empty
                || !participant.IsValid
                || result == null
                || result.Outcome == AntonAwakeningRewardGrantOutcome.Failed)
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (!_plans.TryGetValue(sourceEventId, out var plan)
                    || !plan.Entries.Any(value =>
                        value.Participant.RunIdentity.ParticipantIdentity
                            .Equals(participant)))
                {
                    return false;
                }

                var key = (sourceEventId, participant);
                if (_committed.TryGetValue(key, out var existing))
                {
                    return existing.Outcome == result.Outcome
                        && existing.Reward.GroupKey == result.Reward.GroupKey
                        && existing.Reward.RewardableDungeonId
                            == result.Reward.RewardableDungeonId
                        && existing.Reward.RewardGroupItemId
                            == result.Reward.RewardGroupItemId
                        && existing.Reward.ItemId == result.Reward.ItemId
                        && existing.Reward.Quantity == result.Reward.Quantity
                        && existing.Reward.CardState == result.Reward.CardState;
                }
                _committed.Add(key, result);
                return true;
            }
        }

        internal bool TryGetCommitted(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out AntonAwakeningRewardGrantResult result)
        {
            lock (_syncRoot)
                return _committed.TryGetValue((sourceEventId, participant), out result);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value == DateTime.MinValue || value.Kind == DateTimeKind.Utc)
                return value;
            return value.Kind == DateTimeKind.Local
                ? value.ToUniversalTime()
                : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }
    }
}
