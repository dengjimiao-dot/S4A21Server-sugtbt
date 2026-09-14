using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    internal sealed class AntonAwakeningRewardPlanCreationOutcome
    {
        internal AntonAwakeningRewardPlanCreationOutcome(
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardBatchResolution resolution)
        {
            Plan = plan;
            Resolution = resolution
                ?? new AntonAwakeningRewardBatchResolution(
                    Array.Empty<AntonAwakeningParticipantRewardResolution>());
        }

        internal AntonAwakeningRewardPlan Plan { get; }
        internal AntonAwakeningRewardBatchResolution Resolution { get; }
    }

    // Instance-owned, in-process plan/result state. The event-scoped Lazy is a
    // creation gate: the runtime lock only publishes/reads it, while PVF load,
    // validation and RNG execute once outside the runtime lock.
    internal sealed class AntonAwakeningRewardRuntime
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<
            Guid,
            Lazy<AntonAwakeningRewardPlanCreationOutcome>> _planCreations =
                new Dictionary<
                    Guid,
                    Lazy<AntonAwakeningRewardPlanCreationOutcome>>();
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
            plan = null;
            if (sourceEventId == Guid.Empty
                || roster == null
                || rewards == null)
            {
                return false;
            }

            var rosterSnapshot = roster.ToArray();
            Lazy<AntonAwakeningRewardPlanCreationOutcome> creation;
            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(sourceEventId, out creation))
                {
                    creation = new Lazy<AntonAwakeningRewardPlanCreationOutcome>(
                        () => CreatePlan(
                            sourceEventId,
                            rosterSnapshot,
                            rewards,
                            definition,
                            rewardableDungeonId),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    _planCreations.Add(sourceEventId, creation);
                }
            }

            // Never evaluate the creation factory while holding _syncRoot.
            var outcome = creation.Value;
            plan = outcome.Plan;
            return plan != null && plan.Entries.Count > 0;
        }

        internal bool TryGetPlan(
            Guid sourceEventId,
            out AntonAwakeningRewardPlan plan)
        {
            plan = null;
            Lazy<AntonAwakeningRewardPlanCreationOutcome> creation;
            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(sourceEventId, out creation))
                    return false;
            }

            var outcome = creation.Value;
            plan = outcome.Plan;
            return plan != null && plan.Entries.Count > 0;
        }

        internal bool TryGetParticipantResolution(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            out AntonAwakeningParticipantRewardResolution resolution)
        {
            resolution = null;
            Lazy<AntonAwakeningRewardPlanCreationOutcome> creation;
            lock (_syncRoot)
            {
                if (!_planCreations.TryGetValue(sourceEventId, out creation))
                    return false;
            }

            resolution = creation.Value.Resolution.Participants
                .FirstOrDefault(value => value.Participant.RunIdentity
                    .ParticipantIdentity.Equals(participant));
            return resolution != null;
        }

        internal bool TryRecordProjectionDeadline(
            Guid sourceEventId,
            DungeonParticipantRunIdentity participant,
            DateTime deadlineUtc)
        {
            deadlineUtc = NormalizeUtc(deadlineUtc);
            if (sourceEventId == Guid.Empty
                || !participant.IsValid
                || deadlineUtc == DateTime.MinValue
                || !TryGetPlan(sourceEventId, out var plan)
                || !plan.Entries.Any(value =>
                    value.Participant.RunIdentity.ParticipantIdentity
                        .Equals(participant)))
            {
                return false;
            }

            lock (_syncRoot)
            {
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
                || result.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                || !TryGetPlan(sourceEventId, out var plan)
                || !plan.Entries.Any(value =>
                    value.Participant.RunIdentity.ParticipantIdentity
                        .Equals(participant)))
            {
                return false;
            }

            lock (_syncRoot)
            {
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

        private static AntonAwakeningRewardPlanCreationOutcome CreatePlan(
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            AntonAwakeningDailyCardService rewards,
            SequentialDungeonDefinition definition,
            int rewardableDungeonId)
        {
            AntonAwakeningRewardBatchResolution resolution;
            try
            {
                resolution = rewards.ResolveParticipantRewards(
                    sourceEventId,
                    roster,
                    definition,
                    rewardableDungeonId);
            }
            catch (Exception ex)
            {
                resolution = rewards.FreezeUnexpectedFailure(
                    sourceEventId,
                    roster,
                    definition,
                    rewardableDungeonId,
                    "unexpected " + ex.GetType().Name);
            }

            var entries = resolution.Participants
                .Where(value => value.Succeeded && value.Reward.IsValid)
                .Select(value => new AntonAwakeningRewardPlanEntry(
                    value.Participant,
                    value.Reward))
                .ToList()
                .AsReadOnly();
            var plan = entries.Count > 0
                ? new AntonAwakeningRewardPlan(sourceEventId, entries)
                : null;
            return new AntonAwakeningRewardPlanCreationOutcome(
                plan,
                resolution);
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
