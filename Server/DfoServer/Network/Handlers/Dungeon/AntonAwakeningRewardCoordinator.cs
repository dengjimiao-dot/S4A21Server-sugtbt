using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Coordinates per-participant inventory commits and a shared, generation-safe
    // projection. Durable daily ownership stays in SQLite; packet retries never
    // re-enter the inventory grant transaction.
    internal sealed class AntonAwakeningRewardCoordinator
    {
        internal static readonly TimeSpan PostRevealGrantDelay =
            TimeSpan.FromSeconds(11);

        private readonly AntonAwakeningDailyCardService _dailyRewards;
        private readonly AntonAwakeningRewardGrantService _grants;
        private readonly ISessionDirectory _sessions;
        private readonly InventoryRefreshSender _inventoryRefresh;
        private readonly AntonNormalConquestNotificationSender _sender;
        private readonly TimeSpan _postRevealGrantDelay;

        internal AntonAwakeningRewardCoordinator(
            AntonAwakeningDailyCardService dailyRewards,
            AntonAwakeningRewardGrantService grants,
            ISessionDirectory sessions,
            InventoryRefreshSender inventoryRefresh,
            AntonNormalConquestNotificationSender sender,
            TimeSpan? postRevealGrantDelay = null)
        {
            _dailyRewards = dailyRewards
                ?? throw new ArgumentNullException(nameof(dailyRewards));
            _grants = grants ?? throw new ArgumentNullException(nameof(grants));
            _sessions = sessions;
            _inventoryRefresh = inventoryRefresh;
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _postRevealGrantDelay = postRevealGrantDelay
                ?? PostRevealGrantDelay;
            if (_postRevealGrantDelay < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(postRevealGrantDelay));
            }
        }

        internal async Task PrepareClearAsync(
            DungeonRun sourceRun,
            DungeonClearedFact clearFact)
        {
            if (sourceRun?.Instance == null
                || !TryResolveRewardDefinition(
                    sourceRun,
                    out var rewardDefinition)
                || clearFact == null
                || clearFact.PresentationKind
                    != DungeonClearPresentationKind.Standard)
            {
                return;
            }

            var instance = sourceRun.Instance;
            var sourceIdentity = sourceRun.CaptureIdentity();
            IReadOnlyList<DungeonParticipantRosterEntry> roster = null;
            AntonAwakeningRewardRuntime runtime = null;
            AntonAwakeningRewardPlanCreation creation = null;

            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                if (!IsPreparationContextCurrent(
                        sourceRun,
                        sourceIdentity,
                        instance,
                        clearFact,
                        rewardDefinition,
                        expectedRoster: null))
                {
                    return;
                }

                var journal = instance.ParticipantEffects;
                roster = journal.GetRoster(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance);
                if (roster.Count == 0)
                    return;

                var eligible = roster
                    .Where(value => value != null
                        && !_dailyRewards.HasClaimedRewardToday(
                            value.CharacterId,
                            rewardDefinition.GroupKey,
                            sourceRun.DungeonId))
                    .ToList()
                    .AsReadOnly();
                if (eligible.Count == 0)
                    return;

                runtime = GetOrAttachRuntime(sourceRun);
                if (runtime == null
                    || !runtime.TryGetOrRegisterPlanCreation(
                        clearFact.SourceEventId,
                        eligible,
                        _dailyRewards,
                        rewardDefinition,
                        sourceRun.DungeonId,
                        out creation))
                {
                    return;
                }
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            AntonAwakeningRewardPlanCreationOutcome outcome;
            try
            {
                if (creation == null
                    || !runtime.TryEvaluatePlanCreation(
                        creation,
                        out outcome))
                {
                    FileLogger.Log(
                        $"[AntonAwakening] reward plan unavailable: "
                        + $"instance={instance.PartyDungeonInstanceId} "
                        + $"event={clearFact.SourceEventId:N}");
                    return;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonAwakening] reward planning failed: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N} "
                    + $"error={ex.Message}");
                return;
            }

            var contextIsCurrent = false;
            var published = false;
            await instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                contextIsCurrent = IsPreparationContextCurrent(
                    sourceRun,
                    sourceIdentity,
                    instance,
                    clearFact,
                    rewardDefinition,
                    roster);
                published = contextIsCurrent
                    && ReferenceEquals(
                        instance.Mechanisms.AntonAwakeningReward,
                        runtime)
                    && runtime.TryPublishPlanCreation(creation, outcome);
            }
            finally
            {
                instance.CardRewardProjectionGate.Release();
            }

            if (!contextIsCurrent)
            {
                FileLogger.Log(
                    $"[AntonAwakening] stale reward plan discarded: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N}");
                return;
            }

            var plan = outcome.Plan;
            if (!published || plan == null || plan.Entries.Count == 0)
            {
                FileLogger.Log(
                    $"[AntonAwakening] reward plan unavailable: "
                    + $"instance={instance.PartyDungeonInstanceId} "
                    + $"event={clearFact.SourceEventId:N}");
                return;
            }

            FileLogger.Log(
                $"[AntonAwakening] reward plan prepared: "
                + $"instance={instance.PartyDungeonInstanceId} "
                + $"event={plan.SourceEventId:N} "
                + $"participants={plan.Entries.Count}");
        }

        internal async Task OnFreeCardCommittedAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (!TryResolveParticipantPlan(
                    session,
                    run,
                    out var journal,
                    out var runtime,
                    out var plan,
                    out var entry)
                || !CardRewardRules.IsCommitted(run, CardRewardSide.Free))
            {
                return;
            }

            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                if (!TryResolveParticipantPlan(
                        session,
                        run,
                        out journal,
                        out runtime,
                        out plan,
                        out entry)
                    || !CardRewardRules.IsCommitted(
                        run,
                        CardRewardSide.Free))
                {
                    return;
                }

                var identity = entry.Participant.RunIdentity
                    .ParticipantIdentity;
                var rewardState = journal.GetState(
                    plan.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    identity,
                    DungeonParticipantEffectKinds.AntonAwakeningAutoReward);
                if (rewardState == DungeonParticipantEffectState.Committed)
                {
                    run.Timers.Cancel(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                    return;
                }

                if (!journal.TryBegin(
                        plan.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        entry.Participant,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection,
                        out var reservation,
                        out var existingState))
                {
                    if (existingState
                        == DungeonParticipantEffectState.Committed)
                    {
                        EnsureGrantTimerScheduled(
                            runtime,
                            plan,
                            entry);
                    }
                    return;
                }

                try
                {
                    var reusedProjection = runtime.TryGetProjectionDeadline(
                        plan.SourceEventId,
                        identity,
                        out var deadlineUtc);
                    if (!reusedProjection)
                    {
                        var projected = BuildProjectedEntries(plan);
                        var sent = projected.Count > 0
                            && await _sender.SendAntonAwakeningRewardAsync(
                                session,
                                projected,
                                entry.Participant.RunIdentity);
                        if (!sent)
                        {
                            journal.TryFail(reservation);
                            return;
                        }

                        deadlineUtc = DateTime.UtcNow
                            .Add(_postRevealGrantDelay);
                        if (!runtime.TryRecordProjectionDeadline(
                                plan.SourceEventId,
                                identity,
                                deadlineUtc))
                        {
                            journal.TryFail(reservation);
                            return;
                        }
                    }

                    if (!journal.TryCommit(reservation))
                    {
                        journal.TryFail(reservation);
                        return;
                    }

                    EnsureGrantTimerScheduled(runtime, plan, entry);
                    FileLogger.Log(
                        $"[AntonAwakening] reward projected: "
                        + $"cid={entry.Participant.CharacterId} "
                        + $"userId={entry.Participant.ParticipantUserId} "
                        + $"deadline={deadlineUtc:O} "
                        + $"reused={reusedProjection} "
                        + $"event={plan.SourceEventId:N}");
                }
                catch (Exception ex)
                {
                    journal.TryFail(reservation);
                    FileLogger.Log(
                        $"[AntonAwakening] projection failed: "
                        + $"cid={entry.Participant.CharacterId} "
                        + $"event={plan.SourceEventId:N} "
                        + $"error={ex.Message}");
                }
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }
        }

        internal Task RecoverParticipantAsync(EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (!TryResolveRewardDefinition(run, out _))
            {
                return Task.CompletedTask;
            }

            return OnFreeCardCommittedAsync(session, run);
        }

        private void EnsureGrantTimerScheduled(
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry)
        {
            var run = entry?.Participant?.Run;
            var identity = entry?.Participant?.RunIdentity
                .ParticipantIdentity ?? default;
            if (run == null
                || !runtime.TryGetProjectionDeadline(
                    plan.SourceEventId,
                    identity,
                    out var deadlineUtc))
            {
                return;
            }

            RunTimerTicket ticket;
            if (run.Timers.TryGetSnapshot(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                    out var snapshot)
                && snapshot.HasDeadline
                && snapshot.DeadlineUtc == deadlineUtc)
            {
                if (snapshot.IsSuspended)
                {
                    if (!run.Timers.TryResume(
                            DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                            out ticket,
                            out deadlineUtc))
                    {
                        return;
                    }
                }
                else if (!run.Timers.TryGetCurrentTicket(
                             DungeonRunTimerKeys
                                 .AntonAwakeningPostRevealGrant,
                             out ticket))
                {
                    return;
                }
            }
            else
            {
                ticket = run.Timers.Begin(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                    deadlineUtc,
                    RunTimerDetachPolicy.SuspendUntilResume);
            }

            ScheduleGrantTimer(runtime, plan, entry, deadlineUtc, ticket);
        }

        private void ScheduleGrantTimer(
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            DateTime deadlineUtc,
            RunTimerTicket ticket)
        {
            var run = entry.Participant.Run;
            if (!run.Timers.IsCurrent(ticket))
                return;

            var handle = ClockService.Instance.ScheduleOneShotAsync(
                BuildGrantTimerName(plan, entry, ticket),
                deadlineUtc,
                async _ => await OnGrantTimerElapsedAsync(
                    runtime,
                    plan,
                    entry,
                    ticket));
            run.Timers.Attach(ticket, handle);
        }

        private async Task OnGrantTimerElapsedAsync(
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            RunTimerTicket ticket)
        {
            var run = entry?.Participant?.Run;
            if (run?.Instance == null || !run.Timers.IsCurrent(ticket))
                return;

            await run.Instance.CardRewardProjectionGate.WaitAsync();
            try
            {
                var identity = entry.Participant.RunIdentity
                    .ParticipantIdentity;
                var journal = run.Instance.ParticipantEffects;
                if (!run.Timers.IsCurrent(ticket)
                    || !ReferenceEquals(
                        run.Instance.Mechanisms.AntonAwakeningReward,
                        runtime)
                    || !runtime.TryGetPlan(
                        plan.SourceEventId,
                        out var currentPlan)
                    || !ReferenceEquals(plan, currentPlan)
                    || !run.Matches(entry.Participant.RunIdentity)
                    || !CardRewardRules.IsCommitted(
                        run,
                        CardRewardSide.Free)
                    || journal.GetState(
                        plan.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        identity,
                        DungeonParticipantEffectKinds.DungeonClear)
                        != DungeonParticipantEffectState.Committed
                    || journal.GetState(
                        plan.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        identity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        != DungeonParticipantEffectState.Committed)
                {
                    return;
                }

                if (await TryGrantParticipantAsync(
                        journal,
                        runtime,
                        plan,
                        entry))
                {
                    run.Timers.TryComplete(ticket);
                }
            }
            finally
            {
                run.Instance.CardRewardProjectionGate.Release();
            }
        }

        private async Task<bool> TryGrantParticipantAsync(
            DungeonParticipantEffectJournal journal,
            AntonAwakeningRewardRuntime runtime,
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry)
        {
            var participant = entry.Participant;
            var identity = participant.RunIdentity.ParticipantIdentity;
            if (journal.GetState(
                    plan.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    identity,
                    DungeonParticipantEffectKinds.DungeonClear)
                != DungeonParticipantEffectState.Committed)
            {
                return false;
            }

            if (!journal.TryBegin(
                    plan.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                    out var reservation,
                    out var existingState))
            {
                return existingState == DungeonParticipantEffectState.Committed;
            }

            try
            {
                if (!TryResolveCurrentSession(participant, out var session)
                    || !InventoryContext.TryGetOwnedLease(
                        session.SessionId,
                        participant.CharacterId,
                        out var lease))
                {
                    journal.TryFail(reservation);
                    return false;
                }

                var result = _grants.TryGrant(lease, entry.Reward);
                if (result.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                    || !runtime.TryRecordCommitted(
                        plan.SourceEventId,
                        identity,
                        result))
                {
                    journal.TryFail(reservation);
                    return false;
                }

                if (!journal.TryCommit(reservation))
                {
                    throw new InvalidOperationException(
                        "Anton reward effect reservation was lost after commit.");
                }

                if (result.Outcome == AntonAwakeningRewardGrantOutcome.Granted)
                {
                    try
                    {
                        await SendInventoryRefreshAsync(
                            session,
                            participant,
                            lease,
                            result.Changes);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[AntonAwakening] inventory refresh failed: "
                            + $"cid={participant.CharacterId} "
                            + $"event={plan.SourceEventId:N} "
                            + $"error={ex.Message}");
                    }
                }

                FileLogger.Log(
                    $"[AntonAwakening] reward committed: "
                    + $"cid={participant.CharacterId} "
                    + $"userId={participant.ParticipantUserId} "
                    + $"group={entry.Reward.GroupKey} "
                    + $"rewardGroup={entry.Reward.RewardGroupItemId} "
                    + $"item={entry.Reward.ItemId} "
                    + $"quantity={entry.Reward.Quantity} "
                    + $"state={entry.Reward.CardState} "
                    + $"outcome={result.Outcome} event={plan.SourceEventId:N}");
                return true;
            }
            catch (Exception ex)
            {
                journal.TryFail(reservation);
                FileLogger.Log(
                    $"[AntonAwakening] reward failed: "
                    + $"cid={participant.CharacterId} "
                    + $"event={plan.SourceEventId:N} error={ex.Message}");
                return false;
            }
        }

        private bool TryResolveParticipantPlan(
            EnhancedClientSession session,
            DungeonRun run,
            out DungeonParticipantEffectJournal journal,
            out AntonAwakeningRewardRuntime runtime,
            out AntonAwakeningRewardPlan plan,
            out AntonAwakeningRewardPlanEntry entry)
        {
            journal = null;
            runtime = null;
            plan = null;
            entry = null;
            var player = session?.Player;
            var clearFact = run?.ClearedFact ?? run?.Instance?.ClearedFact;
            if (player == null
                || run?.Instance == null
                || !TryResolveRewardDefinition(run, out _)
                || clearFact?.PresentationKind
                    != DungeonClearPresentationKind.Standard
                || !ReferenceEquals(player.CurrentRun, run)
                || !player.IsCurrentDungeonRun(run.CaptureIdentity())
                || _sessions == null
                || !_sessions.TryGet(player.CharacterId, out var current)
                || !ReferenceEquals(current, session))
            {
                return false;
            }

            journal = run.Instance.ParticipantEffects;
            runtime = run.Instance.Mechanisms.AntonAwakeningReward;
            if (runtime == null
                || !runtime.TryGetPlan(clearFact.SourceEventId, out plan))
                return false;

            var participantIdentity = run.CaptureParticipantIdentity();
            entry = plan.Entries.FirstOrDefault(value =>
                value.Participant.CharacterId == player.CharacterId
                && ReferenceEquals(value.Participant.Run, run)
                && value.Participant.RunIdentity.ParticipantIdentity
                    .Equals(participantIdentity));
            return entry != null
                && journal.GetState(
                    plan.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participantIdentity,
                    DungeonParticipantEffectKinds.DungeonClear)
                    == DungeonParticipantEffectState.Committed;
        }

        private async Task SendInventoryRefreshAsync(
            EnhancedClientSession session,
            DungeonParticipantRosterEntry participant,
            InventoryLease lease,
            InventoryMutationSet changes)
        {
            if (_inventoryRefresh == null || changes == null)
                return;

            foreach (var group in changes.Slots.GroupBy(value => value.ListType))
            {
                if (!IsCurrentParticipantSession(session, participant)
                    || !InventoryContext.IsCurrentLease(
                        lease,
                        session.SessionId,
                        participant.CharacterId))
                {
                    return;
                }
                await _inventoryRefresh.SendUpdateItemList(
                    session,
                    group.Key,
                    group.Select(value => value.SlotIndex));
            }
        }

        private bool TryResolveCurrentSession(
            DungeonParticipantRosterEntry participant,
            out EnhancedClientSession session)
        {
            session = null;
            return participant != null
                && _sessions.TryGet(participant.CharacterId, out session)
                && IsCurrentParticipantSession(session, participant);
        }

        private static bool TryResolveRewardDefinition(
            DungeonRun run,
            out SequentialDungeonDefinition definition)
        {
            definition = run?.Instance?.SequentialDefinition;
            if (definition == null
                || !definition.IsAntonDungeonSequence
                || !definition.ShowIndividualProcess
                || !definition.RewardableDungeonIds.Contains(run.DungeonId))
            {
                definition = null;
                return false;
            }

            // Resolve through the shared catalog as a capability check, then
            // continue using the instance-frozen Definition for this run.
            return SequentialDungeonDefinitionCatalog.Current
                    .TryResolveRewardableByDungeonId(
                        run.DungeonId,
                        out var resolved)
                && ReferenceEquals(resolved, definition);
        }

        private static bool IsPreparationContextCurrent(
            DungeonRun sourceRun,
            DungeonRunIdentity sourceIdentity,
            DungeonInstance instance,
            DungeonClearedFact clearFact,
            SequentialDungeonDefinition rewardDefinition,
            IReadOnlyList<DungeonParticipantRosterEntry> expectedRoster)
        {
            if (sourceRun == null
                || instance == null
                || clearFact == null
                || rewardDefinition == null
                || !ReferenceEquals(sourceRun.Instance, instance)
                || !sourceRun.Matches(sourceIdentity)
                || !clearFact.Source.RunIdentity.Equals(sourceIdentity)
                || !ReferenceEquals(sourceRun.ClearedFact, clearFact)
                || !ReferenceEquals(instance.ClearedFact, clearFact)
                || !ReferenceEquals(
                    instance.SequentialDefinition,
                    rewardDefinition)
                || sourceRun.RunState == DungeonRunState.Ending
                || sourceRun.RunState == DungeonRunState.Ended
                || instance.State == DungeonInstanceState.Ending
                || instance.State == DungeonInstanceState.Ended
                || !instance.ParticipantEffects.TryGetSource(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    out var frozenSource)
                || !ReferenceEquals(frozenSource, clearFact.Source))
            {
                return false;
            }

            if (expectedRoster == null)
                return true;

            var currentRoster = instance.ParticipantEffects.GetRoster(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance);
            return ReferenceEquals(currentRoster, expectedRoster)
                && expectedRoster.Count > 0
                && expectedRoster.All(value => value != null
                    && ReferenceEquals(value.Run?.Instance, instance)
                    && value.Run.Matches(value.RunIdentity));
        }

        internal static bool IsCurrentParticipantSession(
            EnhancedClientSession session,
            DungeonParticipantRosterEntry participant)
        {
            var player = session?.Player;
            return player != null
                && session.TcpClient != null
                && session.TcpClient.Connected
                && ReferenceEquals(player.CurrentRun, participant.Run)
                && player.IsCurrentDungeonRun(participant.RunIdentity)
                && participant.Run.Matches(participant.RunIdentity);
        }

        internal static IReadOnlyList<AntonAwakeningRewardEntry>
            BuildProjectedEntries(AntonAwakeningRewardPlan plan)
        {
            var result = new List<AntonAwakeningRewardEntry>();
            foreach (var entry in plan.Entries)
            {
                result.Add(new AntonAwakeningRewardEntry(
                    entry.Participant.ParticipantUserId,
                    cardType: 0,
                    flags: (uint)entry.Reward.CardState,
                    itemId: (uint)entry.Reward.ItemId,
                    quantity: (uint)entry.Reward.Quantity));
            }
            return result.AsReadOnly();
        }

        private static string BuildGrantTimerName(
            AntonAwakeningRewardPlan plan,
            AntonAwakeningRewardPlanEntry entry,
            RunTimerTicket ticket)
            => "anton-awakening:"
               + entry.Participant.CharacterId
               + ":"
               + plan.SourceEventId.ToString("N")
               + ":"
               + ticket.Generation;

        private static AntonAwakeningRewardRuntime GetOrAttachRuntime(
            DungeonRun run)
        {
            var mechanisms = run?.Instance?.Mechanisms;
            if (mechanisms == null)
                return null;
            var existing = mechanisms.AntonAwakeningReward;
            if (existing != null)
                return existing;

            var created = new AntonAwakeningRewardRuntime();
            return mechanisms.TryAttachAntonAwakeningReward(created)
                ? created
                : mechanisms.AntonAwakeningReward;
        }
    }
}
