using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningAutoRewardSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_AUTO_REWARD selftest ===");
            var failures = 0;
            VerifyStableInstancePlanAndJournal(ref failures);
            VerifyGenerationSafePartyPacketSender(ref failures);
            VerifyPreparationPlanningRunsOutsideProjectionGate(ref failures);
            VerifyStalePreparationIsNotPublished(ref failures);
            VerifyFourParticipantIndependentPlanning(ref failures);
            VerifyParticipantFailureIsolation(ref failures);
            VerifyDelayedProjectionState(ref failures);
            VerifyProjectionJournalRecovery(ref failures);
            VerifyTimerGrantAfterProjection(ref failures);
            VerifyTransactionalGrant(ref failures);
            VerifyNonRewardableDungeonDoesNotPrepare(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_AUTO_REWARD selftest passed."
                    : $"ANTON_AWAKENING_AUTO_REWARD selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyGenerationSafePartyPacketSender(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var leftRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var rightRun = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var leftParticipant = new DungeonParticipantRosterEntry(
                61901,
                101,
                leftRun,
                leftRun.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var rightParticipant = new DungeonParticipantRosterEntry(
                61902,
                202,
                rightRun,
                rightRun.CaptureIdentity(),
                room,
                1,
                partySlot: 1);
            IReadOnlyList<DungeonParticipantRosterEntry> reversedRoster =
                new[] { rightParticipant, leftParticipant };
            var entries = new[]
            {
                new Network.Builders.AntonAwakeningRewardEntry(
                    101, 0, 0, 10157831, 1),
                new Network.Builders.AntonAwakeningRewardEntry(
                    202, 0, 2, 10157833, 1),
            };
            var sessions = new SessionDirectory();

            using (var left = new ConnectedSession())
            using (var right = new ConnectedSession())
            {
                left.Session.Player.CharacterId = leftParticipant.CharacterId;
                left.Session.Player.UserId =
                    leftParticipant.ParticipantUserId;
                left.Session.Player.CurrentRun = leftRun;
                right.Session.Player.CharacterId = rightParticipant.CharacterId;
                right.Session.Player.UserId =
                    rightParticipant.ParticipantUserId;
                right.Session.Player.CurrentRun = rightRun;
                sessions.Register(leftParticipant.CharacterId, left.Session);
                sessions.Register(rightParticipant.CharacterId, right.Session);

                var sender = new AntonNormalConquestNotificationSender(
                    new PartyPacketSender(sessions));
                var result = sender.SendAntonAwakeningRewardToPartyAsync(
                        reversedRoster,
                        entries)
                    .GetAwaiter()
                    .GetResult();
                var leftPackets = left.ReadPackets(2);
                var rightPackets = right.ReadPackets(2);
                Check(
                    "both participants receive projection in stable roster order",
                    result.Succeeded.Count == 2
                    && result.Failed.Count == 0
                    && ReferenceEquals(
                        result.Succeeded[0],
                        leftParticipant)
                    && ReferenceEquals(
                        result.Succeeded[1],
                        rightParticipant),
                    ref failures);
                Check(
                    "party receives byte-identical packet batch",
                    leftPackets.Count == 2
                    && rightPackets.Count == 2
                    && leftPackets[0].SequenceEqual(rightPackets[0])
                    && leftPackets[1].SequenceEqual(rightPackets[1]),
                    ref failures);

                var emptyBatchResult = new PartyPacketSender(sessions)
                    .SendToPartyAsync(
                        reversedRoster,
                        Array.Empty<byte[]>())
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "empty packet batch fails every participant without writing",
                    emptyBatchResult.Succeeded.Count == 0
                    && emptyBatchResult.Failed.Count == 2
                    && ReferenceEquals(
                        emptyBatchResult.Failed[0],
                        leftParticipant)
                    && ReferenceEquals(
                        emptyBatchResult.Failed[1],
                        rightParticipant)
                    && left.AvailableByteCount == 0
                    && right.AvailableByteCount == 0,
                    ref failures);

                var mutableSucceeded = new List<
                    DungeonParticipantRosterEntry> { leftParticipant };
                var mutableFailed = new List<
                    DungeonParticipantRosterEntry> { rightParticipant };
                var frozenResult = new PartyPacketSendResult(
                    mutableSucceeded,
                    mutableFailed);
                mutableSucceeded.Clear();
                mutableFailed.Clear();
                Check(
                    "packet result owns frozen roster snapshots",
                    frozenResult.Succeeded.Count == 1
                    && ReferenceEquals(
                        frozenResult.Succeeded[0],
                        leftParticipant)
                    && frozenResult.Failed.Count == 1
                    && ReferenceEquals(
                        frozenResult.Failed[0],
                        rightParticipant),
                    ref failures);

                right.Session.Player.CurrentRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    rightRun.RunGeneration + 1,
                    DungeonRunState.Active);
                var staleResult = sender
                    .SendAntonAwakeningRewardToPartyAsync(
                        reversedRoster,
                        entries)
                    .GetAwaiter()
                    .GetResult();
                var validRetryPackets = left.ReadPackets(2);
                Check(
                    "stale participant fails without interrupting valid peer",
                    staleResult.Succeeded.Count == 1
                    && ReferenceEquals(
                        staleResult.Succeeded[0],
                        leftParticipant)
                    && staleResult.Failed.Count == 1
                    && ReferenceEquals(
                        staleResult.Failed[0],
                        rightParticipant)
                    && validRetryPackets.Count == 2
                    && right.AvailableByteCount == 0,
                    ref failures);

                right.Session.Player.CurrentRun = rightRun;
                using (var sendLockHeld = new ManualResetEventSlim())
                using (var releaseSendLock = new ManualResetEventSlim())
                {
                    var blocker = System.Threading.Tasks.Task.Run(() =>
                        right.Session.TrySendPacketAsync(
                                Array.Empty<byte>(),
                                CancellationToken.None,
                                () =>
                                {
                                    sendLockHeld.Set();
                                    releaseSendLock.Wait(
                                        TimeSpan.FromSeconds(10));
                                    return false;
                                })
                            .GetAwaiter()
                            .GetResult());
                    var lockWasHeld = sendLockHeld.Wait(
                        TimeSpan.FromSeconds(5));
                    var queued = new PartyPacketSender(sessions)
                        .SendToPartyAsync(
                            new[] { rightParticipant },
                            new[] { leftPackets[0], leftPackets[1] });
                    var queuedBehindSendLock = !queued.IsCompleted;
                    right.Session.Player.CurrentRun = new DungeonRun(
                        instance,
                        DungeonIdentityGenerator.NextRunId(),
                        rightRun.RunGeneration + 1,
                        DungeonRunState.Active);
                    releaseSendLock.Set();
                    System.Threading.Tasks.Task.WaitAll(
                        new System.Threading.Tasks.Task[] { blocker, queued },
                        TimeSpan.FromSeconds(10));
                    var queuedResult = queued.GetAwaiter().GetResult();
                    Check(
                        "send-lock queued stale generation writes no old packet",
                        lockWasHeld
                        && queuedBehindSendLock
                        && queuedResult.Succeeded.Count == 0
                        && queuedResult.Failed.Count == 1
                        && ReferenceEquals(
                            queuedResult.Failed[0],
                            rightParticipant)
                        && right.AvailableByteCount == 0,
                        ref failures);
                }

                sessions.UnregisterAsync(
                        leftParticipant.CharacterId,
                        left.Session)
                    .GetAwaiter()
                    .GetResult();
                sessions.UnregisterAsync(
                        rightParticipant.CharacterId,
                        right.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyNonRewardableDungeonDoesNotPrepare(
            ref int failures)
        {
            var instance = new DungeonInstance(4108, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var source = DungeonEventEnvelope.Create(
                run,
                63501,
                "non-rewardable-sequential",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            run.TryBeginClearCommit(clearFact);
            run.TryCompleteClearCommit(clearFact);
            var participant = new DungeonParticipantRosterEntry(
                63501,
                901,
                run,
                run.CaptureIdentity(),
                new DungeonRoomIdentity(instance.Identity, 1),
                1,
                partySlot: 0);
            instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                new[] { participant },
                out _);

            var service = new AntonAwakeningDailyCardService(
                null,
                _ => null,
                _ => 0);
            var coordinator = new AntonAwakeningRewardCoordinator(
                service,
                new AntonAwakeningRewardGrantService(service),
                null,
                null,
                new AntonNormalConquestNotificationSender());
            coordinator.PrepareClearAsync(run, clearFact)
                .GetAwaiter()
                .GetResult();
            Check(
                "non-rewardable sequential dungeon does not prepare Anton rewards",
                instance.Mechanisms.AntonAwakeningReward == null,
                ref failures);
        }

        private static void VerifyPreparationPlanningRunsOutsideProjectionGate(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 4, characterIdBase: 63600);
            var sourceRun = roster[0].Run;
            var source = DungeonEventEnvelope.Create(
                sourceRun,
                roster[0].CharacterId,
                "anton-plan-lock-boundary",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            sourceRun.TryBeginClearCommit(clearFact);
            sourceRun.TryCompleteClearCommit(clearFact);
            instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);

            using (var loaderEntered = new ManualResetEventSlim())
            using (var releaseLoader = new ManualResetEventSlim())
            {
                var rollCalls = 0;
                var rewards = new AntonAwakeningDailyCardService(
                    null,
                    _ =>
                    {
                        loaderEntered.Set();
                        releaseLoader.Wait(TimeSpan.FromSeconds(10));
                        return BuildUpgradableLegacy((90001, 1, 1));
                    },
                    _ =>
                    {
                        Interlocked.Increment(ref rollCalls);
                        return 0;
                    });
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    null,
                    null,
                    new AntonNormalConquestNotificationSender());
                System.Threading.Tasks.Task first = null;
                System.Threading.Tasks.Task second = null;
                var planningStarted = false;
                var gateAvailable = false;
                try
                {
                    first = System.Threading.Tasks.Task.Run(() =>
                        coordinator.PrepareClearAsync(sourceRun, clearFact)
                            .GetAwaiter()
                            .GetResult());
                    planningStarted = loaderEntered.Wait(
                        TimeSpan.FromSeconds(5));
                    second = System.Threading.Tasks.Task.Run(() =>
                        coordinator.PrepareClearAsync(sourceRun, clearFact)
                            .GetAwaiter()
                            .GetResult());
                    gateAvailable = instance.CardRewardProjectionGate.Wait(
                        TimeSpan.FromSeconds(1));
                    if (gateAvailable)
                        instance.CardRewardProjectionGate.Release();
                }
                finally
                {
                    releaseLoader.Set();
                    if (first != null && second != null)
                    {
                        System.Threading.Tasks.Task.WaitAll(
                            new[] { first, second },
                            TimeSpan.FromSeconds(10));
                    }
                }

                var runtime = instance.Mechanisms.AntonAwakeningReward;
                Check(
                    "STK planning runs outside the instance card projection gate",
                    planningStarted && gateAvailable,
                    ref failures);
                Check(
                    "concurrent clear preparation evaluates one four-member plan",
                    first?.IsCompletedSuccessfully == true
                    && second?.IsCompletedSuccessfully == true
                    && rollCalls == 8
                    && runtime != null
                    && runtime.TryGetPlan(
                        clearFact.SourceEventId,
                        out var plan)
                    && plan.Entries.Count == 4,
                    ref failures);
            }
        }

        private static void VerifyStalePreparationIsNotPublished(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 1, characterIdBase: 63700);
            var sourceRun = roster[0].Run;
            var source = DungeonEventEnvelope.Create(
                sourceRun,
                roster[0].CharacterId,
                "anton-stale-plan",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            sourceRun.TryBeginClearCommit(clearFact);
            sourceRun.TryCompleteClearCommit(clearFact);
            instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);

            using (var rollEntered = new ManualResetEventSlim())
            using (var releaseRoll = new ManualResetEventSlim())
            {
                var rollCalls = 0;
                var rewards = new AntonAwakeningDailyCardService(
                    null,
                    _ => BuildUpgradableLegacy((90001, 1, 1)),
                    _ =>
                    {
                        var call = Interlocked.Increment(ref rollCalls);
                        if (call == 1)
                        {
                            rollEntered.Set();
                            releaseRoll.Wait(TimeSpan.FromSeconds(10));
                        }
                        return 0;
                    });
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    null,
                    null,
                    new AntonNormalConquestNotificationSender());
                var prepare = System.Threading.Tasks.Task.Run(() =>
                    coordinator.PrepareClearAsync(sourceRun, clearFact)
                        .GetAwaiter()
                        .GetResult());
                var planningStarted = rollEntered.Wait(
                    TimeSpan.FromSeconds(5));
                var gateAvailable = instance.CardRewardProjectionGate.Wait(
                    TimeSpan.FromSeconds(1));
                try
                {
                    sourceRun.TryBeginEnding();
                }
                finally
                {
                    if (gateAvailable)
                        instance.CardRewardProjectionGate.Release();
                    releaseRoll.Set();
                }
                prepare.Wait(TimeSpan.FromSeconds(10));

                var replacementRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    sourceRun.RunGeneration + 1,
                    DungeonRunState.Active);
                coordinator.PrepareClearAsync(replacementRun, clearFact)
                    .GetAwaiter()
                    .GetResult();
                var runtime = instance.Mechanisms.AntonAwakeningReward;
                Check(
                    "planning completion for an ending run is not published",
                    planningStarted
                    && gateAvailable
                    && prepare.IsCompletedSuccessfully
                    && rollCalls == 2
                    && instance.State == DungeonInstanceState.Cleared
                    && runtime != null
                    && !runtime.TryGetPlan(clearFact.SourceEventId, out _),
                    ref failures);
            }
        }

        private static void VerifyProjectionJournalRecovery(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var source = DungeonEventEnvelope.Create(
                run,
                63201,
                "anton-projection-journal-recovery",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            run.TryBeginClearCommit(clearFact);
            run.TryCompleteClearCommit(clearFact);
            var participant = new DungeonParticipantRosterEntry(
                63201,
                405,
                run,
                run.CaptureIdentity(),
                new DungeonRoomIdentity(instance.Identity, 1),
                1,
                partySlot: 0);
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                new[] { participant },
                out _);
            journal.TryBegin(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance,
                participant,
                DungeonParticipantEffectKinds.DungeonClear,
                out var clearReservation,
                out _);
            journal.TryCommit(clearReservation);
            run.Effects.TryReserve(
                CardRewardRules.GetEffectId(run, CardRewardSide.Free),
                out var freeReservation);
            run.Effects.TryCommit(freeReservation);

            var rewards = CreateRewardService(
                dailyReset: null,
                finalItemId: 3309,
                quantity: 2);
            var sessions = new SessionDirectory();
            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = participant.CharacterId;
                capture.Session.Player.UserId = participant.ParticipantUserId;
                capture.Session.Player.CurrentRun = run;
                sessions.Register(participant.CharacterId, capture.Session);
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    sessions,
                    null,
                    new AntonNormalConquestNotificationSender());
                coordinator.PrepareClearAsync(run, clearFact)
                    .GetAwaiter()
                    .GetResult();

                var runtime = instance.Mechanisms.AntonAwakeningReward;
                var frozenDeadline = DateTime.UtcNow.AddMinutes(2);
                var deadlineRecorded = runtime != null
                    && runtime.TryRecordProjectionDeadline(
                        clearFact.SourceEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        frozenDeadline);
                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();

                Check(
                    "sent projection with lost journal reservation reuses its original deadline",
                    deadlineRecorded
                    && capture.AvailableByteCount == 0
                    && journal.GetState(
                        clearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed
                    && run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var timerSnapshot)
                    && timerSnapshot.DeadlineUtc == frozenDeadline,
                    ref failures);

                run.Timers.Cancel(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                sessions.UnregisterAsync(
                        participant.CharacterId,
                        capture.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyTimerGrantAfterProjection(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_delayed_grant_{Guid.NewGuid():N}.db");
            var sessionId = Guid.Empty;
            const int accountId = 63100;
            const int characterId = 63101;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }

                var instance = new DungeonInstance(247, 0);
                var run = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                var source = DungeonEventEnvelope.Create(
                    run,
                    characterId,
                    "anton-delayed-grant",
                    sourceEventId: Guid.NewGuid());
                var clearFact = instance.GetOrCreateClearedFact(
                    new DungeonClearIntent(source, "selftest", 0),
                    out _);
                run.TryBeginClearCommit(clearFact);
                run.TryCompleteClearCommit(clearFact);
                var participant = new DungeonParticipantRosterEntry(
                    characterId,
                    404,
                    run,
                    run.CaptureIdentity(),
                    new DungeonRoomIdentity(instance.Identity, 1),
                    1,
                    partySlot: 0);
                instance.ParticipantEffects.TryFreeze(
                    clearFact.Source,
                    DungeonParticipantEffectAudience.Instance,
                    new[] { participant },
                    out _);
                instance.ParticipantEffects.TryBegin(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.DungeonClear,
                    out var clearReservation,
                    out _);
                instance.ParticipantEffects.TryCommit(clearReservation);
                run.Effects.TryReserve(
                    CardRewardRules.GetEffectId(run, CardRewardSide.Free),
                    out var freeReservation);
                run.Effects.TryCommit(freeReservation);

                var daily = CreateRewardService(
                    new DailyResetService(database),
                    finalItemId: 3309,
                    quantity: 3);
                var sessions = new SessionDirectory();
                using (var capture = new ConnectedSession())
                {
                    capture.Session.Player.CharacterId = characterId;
                    capture.Session.Player.UserId = 404;
                    capture.Session.Player.CurrentRun = run;
                    sessionId = capture.Session.SessionId;
                    var lease = InventoryContext.Register(
                        sessionId,
                        characterId,
                        inventory);
                    sessions.Register(characterId, capture.Session);
                    var coordinator = new AntonAwakeningRewardCoordinator(
                        daily,
                        new AntonAwakeningRewardGrantService(daily),
                        sessions,
                        null,
                        new AntonNormalConquestNotificationSender(),
                        postRevealGrantDelay: TimeSpan.FromMilliseconds(300));

                    coordinator.PrepareClearAsync(run, clearFact)
                        .GetAwaiter()
                        .GetResult();
                    coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                        .GetAwaiter()
                        .GetResult();
                    var projectionPackets = capture.ReadPackets(2);
                    Check(
                        "Anton inventory and daily claim remain absent before deadline",
                        projectionPackets.Count == 2
                        && BitConverter.ToUInt32(projectionPackets[0], 26)
                            == 3309
                        && BitConverter.ToUInt32(projectionPackets[0], 30)
                            == 3
                        && CountMainItem(lease, 3309) == 0
                        && !daily.HasClaimedRewardToday(characterId, 41, 247),
                        ref failures);

                    ClockService.Instance.CheckOnce(
                        DateTime.UtcNow.AddSeconds(1));
                    var grantDeadline = DateTime.UtcNow.AddSeconds(3);
                    while (!daily.HasClaimedRewardToday(characterId, 41, 247)
                           && DateTime.UtcNow < grantDeadline)
                    {
                        Thread.Sleep(25);
                    }
                    var grantedCount = CountMainItem(lease, 3309);
                    Check(
                        "Anton timer commits the frozen reward and daily claim once",
                        daily.HasClaimedRewardToday(characterId, 41, 247)
                        && grantedCount == 3
                        && instance.ParticipantEffects.GetState(
                            clearFact.SourceEventId,
                            DungeonParticipantEffectAudience.Instance,
                            participant.RunIdentity.ParticipantIdentity,
                            DungeonParticipantEffectKinds
                                .AntonAwakeningAutoReward)
                            == DungeonParticipantEffectState.Committed,
                        ref failures);

                    coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                        .GetAwaiter()
                        .GetResult();
                    Thread.Sleep(100);
                    Check(
                        "committed Anton reward ignores duplicate free-card callbacks",
                        CountMainItem(lease, 3309) == grantedCount,
                        ref failures);
                    run.Timers.Cancel(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                    sessions.UnregisterAsync(characterId, capture.Session)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            finally
            {
                if (sessionId != Guid.Empty)
                    InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void VerifyDelayedProjectionState(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var run = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var source = DungeonEventEnvelope.Create(
                run,
                63001,
                "anton-delayed-projection",
                sourceEventId: Guid.NewGuid());
            var clearFact = instance.GetOrCreateClearedFact(
                new DungeonClearIntent(source, "selftest", 0),
                out _);
            run.TryBeginClearCommit(clearFact);
            run.TryCompleteClearCommit(clearFact);
            var participant = new DungeonParticipantRosterEntry(
                63001,
                303,
                run,
                run.CaptureIdentity(),
                room,
                1,
                partySlot: 0);
            var roster = new[] { participant };
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);
            journal.TryBegin(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance,
                participant,
                DungeonParticipantEffectKinds.DungeonClear,
                out var clearReservation,
                out _);
            journal.TryCommit(clearReservation);

            var rewards = CreateRewardService(
                dailyReset: null,
                finalItemId: 3309,
                quantity: 2);
            var sessions = new SessionDirectory();
            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = participant.CharacterId;
                capture.Session.Player.UserId = participant.ParticipantUserId;
                capture.Session.Player.CurrentRun = run;
                sessions.Register(participant.CharacterId, capture.Session);
                var coordinator = new AntonAwakeningRewardCoordinator(
                    rewards,
                    new AntonAwakeningRewardGrantService(rewards),
                    sessions,
                    null,
                    new AntonNormalConquestNotificationSender());

                coordinator.PrepareClearAsync(run, clearFact)
                    .GetAwaiter()
                    .GetResult();
                var runtime = instance.Mechanisms.AntonAwakeningReward;
                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "Anton projection waits for committed free-card reward",
                    runtime != null
                    && !runtime.TryGetProjectionDeadline(
                        clearFact.SourceEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        out _)
                    && journal.GetState(
                        clearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Pending
                    && capture.AvailableByteCount == 0,
                    ref failures);

                run.Effects.TryReserve(
                    CardRewardRules.GetEffectId(run, CardRewardSide.Free),
                    out var freeReservation);
                run.Effects.TryCommit(freeReservation);
                var projectedAt = DateTime.UtcNow;
                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                var packets = capture.ReadPackets(2);
                var projectedUntil = DateTime.UtcNow.AddSeconds(11);
                var hasDeadline = runtime.TryGetProjectionDeadline(
                    clearFact.SourceEventId,
                    participant.RunIdentity.ParticipantIdentity,
                    out var deadlineUtc);
                Check(
                    "committed free card projects 0x0319 then 0x00FF and arms 11-second timer",
                    AntonAwakeningRewardCoordinator.PostRevealGrantDelay
                        == TimeSpan.FromSeconds(11)
                    && packets.Count == 2
                    && BitConverter.ToUInt16(packets[0], 1)
                        == (ushort)NotiPacketTypeA21
                            .ANTON_AWAKENING_MODE_REWARD
                    && BitConverter.ToUInt16(packets[1], 1)
                        == (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR
                    && hasDeadline
                    && deadlineUtc >= projectedAt.AddSeconds(11)
                    && deadlineUtc <= projectedUntil
                    && journal.GetState(
                        clearFact.SourceEventId,
                        DungeonParticipantEffectAudience.Instance,
                        participant.RunIdentity.ParticipantIdentity,
                        DungeonParticipantEffectKinds
                            .AntonAwakeningRewardProjection)
                        == DungeonParticipantEffectState.Committed
                    && run.Timers.TryGetSnapshot(
                        DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                        out var timerSnapshot)
                    && timerSnapshot.DeadlineUtc == deadlineUtc
                    && timerSnapshot.DetachPolicy
                        == RunTimerDetachPolicy.SuspendUntilResume,
                    ref failures);

                coordinator.OnFreeCardCommittedAsync(capture.Session, run)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "duplicate free-card callback does not reproject or replace deadline",
                    capture.AvailableByteCount == 0
                    && runtime.TryGetProjectionDeadline(
                        clearFact.SourceEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        out var replayDeadline)
                    && replayDeadline == deadlineUtc,
                    ref failures);

                var suspended = run.Timers.SuspendForNetworkDetach();
                var resumed = run.Timers.TryResume(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant,
                    out _,
                    out var resumedDeadline);
                Check(
                    "network detach and resume preserve the original Anton deadline",
                    suspended == 1
                    && resumed
                    && resumedDeadline == deadlineUtc,
                    ref failures);
                run.Timers.Cancel(
                    DungeonRunTimerKeys.AntonAwakeningPostRevealGrant);
                sessions.UnregisterAsync(
                        participant.CharacterId,
                        capture.Session)
                    .GetAwaiter()
                    .GetResult();
            }
        }

        private static void VerifyStableInstancePlanAndJournal(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var runA = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var runB = new DungeonRun(
                instance,
                DungeonIdentityGenerator.NextRunId(),
                1,
                DungeonRunState.Active);
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var roster = new List<DungeonParticipantRosterEntry>
            {
                new DungeonParticipantRosterEntry(
                    62001,
                    202,
                    runB,
                    runB.CaptureIdentity(),
                    room,
                    1,
                    partySlot: 1),
                new DungeonParticipantRosterEntry(
                    62000,
                    101,
                    runA,
                    runA.CaptureIdentity(),
                    room,
                    1,
                    partySlot: 0),
            };
            var definition = BuildRewardDefinition(
                "1 7001 1 1 7002 2");
            var rolls = new Queue<int>(new[] { 0, 0, 1, 0 });
            var drawCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                groupItemId => groupItemId == 7001
                    ? BuildUpgradableLegacy((10157834, 1, 2))
                    : BuildUpgradableLegacy((10157833, 1, 4)),
                maximum =>
                {
                    drawCalls++;
                    var value = rolls.Dequeue();
                    return value < maximum ? value : maximum - 1;
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var eventId = Guid.NewGuid();

            var first = TryCreateAndPublishPlan(
                runtime,
                eventId,
                roster,
                rewards,
                definition,
                247,
                out var firstPlan);
            var second = TryCreateAndPublishPlan(
                runtime,
                eventId,
                new[] { roster[0] },
                rewards,
                definition,
                247,
                out var secondPlan);
            Check(
                "same clear event reuses one immutable participant plan",
                first
                && second
                && ReferenceEquals(firstPlan, secondPlan)
                && firstPlan.Entries.Count == 2
                && drawCalls == 4,
                ref failures);
            Check(
                "reward plan is ordered by frozen party slot",
                firstPlan?.Entries[0].Participant.ParticipantUserId == 101
                && firstPlan?.Entries[1].Participant.ParticipantUserId == 202,
                ref failures);
            Check(
                "reward plan preserves item and PVF state",
                firstPlan?.Entries[0].Reward.ItemId == 10157834
                && firstPlan.Entries[0].Reward.State == 1
                && firstPlan.Entries[0].Reward.Quantity == 2
                && firstPlan.Entries[1].Reward.ItemId == 10157833
                && firstPlan.Entries[1].Reward.State == 2
                && firstPlan.Entries[1].Reward.Quantity == 4,
                ref failures);
            Check(
                "reward plan lookup returns the frozen event plan",
                runtime.TryGetPlan(eventId, out var lookedUpPlan)
                && ReferenceEquals(firstPlan, lookedUpPlan),
                ref failures);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            var replacedDeadline = deadline.AddSeconds(30);
            var recordedDeadline = runtime.TryRecordProjectionDeadline(
                eventId,
                roster[1].RunIdentity.ParticipantIdentity,
                deadline);
            var replayedDeadline = runtime.TryRecordProjectionDeadline(
                eventId,
                roster[1].RunIdentity.ParticipantIdentity,
                deadline);
            var replacementRejected = !runtime.TryRecordProjectionDeadline(
                eventId,
                roster[1].RunIdentity.ParticipantIdentity,
                replacedDeadline);
            Check(
                "projection deadline is absolute and immutable per participant",
                recordedDeadline
                && replayedDeadline
                && replacementRejected
                && runtime.TryGetProjectionDeadline(
                    eventId,
                    roster[1].RunIdentity.ParticipantIdentity,
                    out var storedDeadline)
                && storedDeadline == deadline,
                ref failures);

            var source = new DungeonEventEnvelope(
                eventId,
                runA.CaptureIdentity(),
                room.RoomInstanceId,
                62000,
                62000,
                null,
                null,
                "anton-selftest",
                1);
            var journal = instance.ParticipantEffects;
            journal.TryFreeze(
                source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);
            var began = journal.TryBegin(
                eventId,
                DungeonParticipantEffectAudience.Instance,
                roster[1],
                DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                out var failedReservation,
                out _);
            var failed = journal.TryFail(failedReservation);
            var retried = journal.TryBegin(
                eventId,
                DungeonParticipantEffectAudience.Instance,
                roster[1],
                DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                out var committedReservation,
                out _);
            var committed = journal.TryCommit(committedReservation);
            var duplicate = journal.TryBegin(
                eventId,
                DungeonParticipantEffectAudience.Instance,
                roster[1],
                DungeonParticipantEffectKinds.AntonAwakeningAutoReward,
                out _,
                out var duplicateState);
            Check(
                "failed reward effect is retryable and committed effect is idempotent",
                began
                && failed
                && retried
                && committed
                && !duplicate
                && duplicateState == DungeonParticipantEffectState.Committed,
                ref failures);

            using (var capture = new ConnectedSession())
            {
                capture.Session.Player.CharacterId = roster[1].CharacterId;
                capture.Session.Player.UserId = roster[1].ParticipantUserId;
                capture.Session.Player.CurrentRun = runA;
                Check(
                    "matching frozen run generation remains projection eligible",
                    Network.Handlers.Dungeon.AntonAwakeningRewardCoordinator
                        .IsCurrentParticipantSession(
                            capture.Session,
                            roster[1]),
                    ref failures);

                var projected = new[]
                {
                    new Network.Builders.AntonAwakeningRewardEntry(
                        101, 0, 0, 10157831, 1),
                    new Network.Builders.AntonAwakeningRewardEntry(
                        202, 0, 2, 10157833, 1),
                };
                var sent = new Network.Handlers.Dungeon
                    .AntonNormalConquestNotificationSender()
                    .SendAntonAwakeningRewardAsync(
                        capture.Session,
                        projected,
                        runA.CaptureIdentity())
                    .GetAwaiter()
                    .GetResult();
                var packets = capture.ReadPackets(2);
                var expectedBody = Network.Builders
                    .AntonAwakeningRewardPacketBuilder.Build(projected);
                Check(
                    "sender projects one full two-member 0x0319 body then 0x00FF",
                    sent
                    && packets.Count == 2
                    && packets[0].Length == 15 + expectedBody.Length
                    && BitConverter.ToUInt16(packets[0], 1)
                        == (ushort)NotiPacketTypeA21
                            .ANTON_AWAKENING_MODE_REWARD
                    && packets[0].Skip(15).SequenceEqual(expectedBody)
                    && BitConverter.ToUInt16(packets[1], 1)
                        == (ushort)NotiPacketTypeA21.EXERCISE_MODE_CLEAR
                    && packets[1].Length == 15 + sizeof(uint),
                    ref failures);

                capture.Session.Player.CurrentRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    runA.RunGeneration + 1,
                    DungeonRunState.Active);
                Check(
                    "stale run generation cannot receive old projection",
                    !Network.Handlers.Dungeon.AntonAwakeningRewardCoordinator
                        .IsCurrentParticipantSession(
                            capture.Session,
                            roster[1]),
                    ref failures);
            }
        }

        private static void VerifyTransactionalGrant(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_auto_reward_{Guid.NewGuid():N}.db");
            var sessionId = Guid.NewGuid();
            const int accountId = 62100;
            const int characterId = 62101;
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                Seed(database, accountId, characterId);
                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }
                var lease = InventoryContext.Register(
                    sessionId,
                    characterId,
                    inventory);
                var daily = new AntonAwakeningDailyCardService(
                    new DailyResetService(database),
                    _ => null,
                    _ => 0);
                var grants = new AntonAwakeningRewardGrantService(daily);

                var failed = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        247,
                        7001,
                        int.MaxValue,
                        2,
                        2));
                Check(
                    "failed inventory insertion rolls back daily claim",
                    failed.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                    && !daily.HasClaimedRewardToday(characterId, 99, 247),
                    ref failures);

                var granted = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        247,
                        7001,
                        3309,
                        3,
                        0));
                var countAfterGrant = CountMainItem(lease, 3309);
                var duplicate = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        247,
                        7001,
                        3309,
                        3,
                        0));
                var countAfterDuplicate = CountMainItem(lease, 3309);
                var differentScope = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(
                        99,
                        248,
                        7002,
                        3309,
                        2,
                        1));
                Check(
                    "reward and daily claim commit in one transaction",
                    granted.Outcome == AntonAwakeningRewardGrantOutcome.Granted
                    && daily.HasClaimedRewardToday(characterId, 99, 247)
                    && countAfterGrant == 3
                    && CountMainItem(lease, 7001) == 0,
                    ref failures);
                Check(
                    "duplicate clear becomes committed no-reward",
                    duplicate.Outcome
                        == AntonAwakeningRewardGrantOutcome.AlreadyClaimed
                    && countAfterDuplicate == countAfterGrant,
                    ref failures);
                Check(
                    "a different group/dungeon claim key remains independent",
                    differentScope.Outcome
                        == AntonAwakeningRewardGrantOutcome.Granted
                    && daily.HasClaimedRewardToday(characterId, 99, 248)
                    && CountMainItem(lease, 3309) == countAfterGrant + 2,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, characterId);
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void VerifyFourParticipantIndependentPlanning(
            ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 4, characterIdBase: 64000);
            var definition = BuildRewardDefinition(
                "1 7001 0 1 7002 1");
            var rolls = new Queue<int>(new[]
            {
                0, 0,
                1, 0,
                0, 1,
                1, 1,
            });
            var rollCalls = 0;
            var loadCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                groupItemId =>
                {
                    Interlocked.Increment(ref loadCalls);
                    return groupItemId == 7001
                        ? BuildUpgradableLegacy(
                            (90001, 1, 1),
                            (90002, 1, 2))
                        : BuildUpgradableLegacy(
                            (90003, 1, 3),
                            (90004, 1, 4));
                },
                maximum =>
                {
                    Interlocked.Increment(ref rollCalls);
                    lock (rolls)
                        return rolls.Dequeue();
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var sourceEventId = Guid.NewGuid();
            AntonAwakeningRewardPlan firstPlan = null;
            AntonAwakeningRewardPlan secondPlan = null;

            var first = System.Threading.Tasks.Task.Run(() =>
                TryCreateAndPublishPlan(
                    runtime,
                    sourceEventId,
                    roster,
                    rewards,
                    definition,
                    247,
                    out firstPlan));
            var second = System.Threading.Tasks.Task.Run(() =>
                TryCreateAndPublishPlan(
                    runtime,
                    sourceEventId,
                    roster.Reverse().ToList().AsReadOnly(),
                    rewards,
                    definition,
                    247,
                    out secondPlan));
            System.Threading.Tasks.Task.WaitAll(first, second);

            Check(
                "concurrent event planning publishes one immutable four-member plan",
                first.Result
                && second.Result
                && ReferenceEquals(firstPlan, secondPlan)
                && firstPlan.Entries.Count == 4
                && loadCalls == 2
                && rollCalls == 8
                && rolls.Count == 0,
                ref failures);
            Check(
                "four participants receive independent two-stage final rewards",
                firstPlan != null
                && firstPlan.Entries.Select(value => value.Reward.ItemId)
                    .SequenceEqual(new[] { 90001, 90003, 90002, 90004 })
                && firstPlan.Entries.Select(value => value.Reward.Quantity)
                    .SequenceEqual(new[] { 1, 3, 2, 4 }),
                ref failures);

            var projected = AntonAwakeningRewardCoordinator
                .BuildProjectedEntries(firstPlan);
            Check(
                "projection uses each frozen final item and quantity",
                projected.Select(value => value.ItemId)
                    .SequenceEqual(new uint[] { 90001, 90003, 90002, 90004 })
                && projected.Select(value => value.Quantity)
                    .SequenceEqual(new uint[] { 1, 3, 2, 4 })
                && projected.All(value => value.ItemId != 7001
                    && value.ItemId != 7002),
                ref failures);
        }

        private static void VerifyParticipantFailureIsolation(ref int failures)
        {
            var instance = new DungeonInstance(247, 0);
            var roster = BuildRoster(instance, 3, characterIdBase: 65000);
            var definition = BuildRewardDefinition("1 7001 0");
            var rolls = new Queue<int>(new[]
            {
                0, 0,
                0, 1,
                0, 0,
            });
            var rollCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                _ => BuildUpgradableLegacy((90001, 1, 2)),
                maximum =>
                {
                    rollCalls++;
                    var value = rolls.Dequeue();
                    return value < maximum ? value : maximum;
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var sourceEventId = Guid.NewGuid();
            var created = TryCreateAndPublishPlan(
                runtime,
                sourceEventId,
                roster,
                rewards,
                definition,
                247,
                out var plan);
            var replayed = TryCreateAndPublishPlan(
                runtime,
                sourceEventId,
                roster,
                rewards,
                definition,
                247,
                out var replayedPlan);
            Check(
                "one participant draw failure does not discard successful peers",
                created
                && replayed
                && ReferenceEquals(plan, replayedPlan)
                && plan.Entries.Count == 2
                && plan.Entries.Select(value => value.Participant.CharacterId)
                    .SequenceEqual(new[] { 65000, 65002 })
                && rollCalls == 6,
                ref failures);
            Check(
                "failed participant terminal result is frozen for the event",
                runtime.TryGetParticipantResolution(
                    sourceEventId,
                    roster[1].RunIdentity.ParticipantIdentity,
                    out var failedResolution)
                && !failedResolution.Succeeded
                && failedResolution.Failure.RewardGroupItemId == 7001
                && failedResolution.Failure.CardState == 0,
                ref failures);

            var allFailedRollCalls = 0;
            var allFailedRewards = new AntonAwakeningDailyCardService(
                null,
                _ => BuildUpgradableLegacy((90001, 1, 2)),
                maximum =>
                {
                    allFailedRollCalls++;
                    return maximum;
                });
            var allFailedRuntime = new AntonAwakeningRewardRuntime();
            var allFailedEventId = Guid.NewGuid();
            var allFailed = TryCreateAndPublishPlan(
                allFailedRuntime,
                allFailedEventId,
                roster,
                allFailedRewards,
                definition,
                247,
                out _);
            var allFailedReplay = TryCreateAndPublishPlan(
                allFailedRuntime,
                allFailedEventId,
                roster,
                allFailedRewards,
                definition,
                247,
                out _);
            Check(
                "all-failed planning attempt is terminal and never rerolls",
                !allFailed
                && !allFailedReplay
                && allFailedRollCalls == roster.Count
                && roster.All(participant =>
                    allFailedRuntime.TryGetParticipantResolution(
                        allFailedEventId,
                        participant.RunIdentity.ParticipantIdentity,
                        out var resolution)
                    && !resolution.Succeeded),
                ref failures);
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry> BuildRoster(
            DungeonInstance instance,
            int count,
            int characterIdBase)
        {
            var room = new DungeonRoomIdentity(instance.Identity, 1);
            var roster = new List<DungeonParticipantRosterEntry>();
            for (var index = 0; index < count; index++)
            {
                var run = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    1,
                    DungeonRunState.Active);
                roster.Add(new DungeonParticipantRosterEntry(
                    characterIdBase + index,
                    (ushort)(500 + index),
                    run,
                    run.CaptureIdentity(),
                    room,
                    1,
                    partySlot: (byte)index));
            }
            return roster.AsReadOnly();
        }

        private static bool TryCreateAndPublishPlan(
            AntonAwakeningRewardRuntime runtime,
            Guid sourceEventId,
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            AntonAwakeningDailyCardService rewards,
            DfoServer.GameWorld.SequentialDungeonDefinition definition,
            int rewardableDungeonId,
            out AntonAwakeningRewardPlan plan)
        {
            plan = null;
            if (runtime == null
                || !runtime.TryGetOrRegisterPlanCreation(
                    sourceEventId,
                    roster,
                    rewards,
                    definition,
                    rewardableDungeonId,
                    out var creation)
                || !runtime.TryEvaluatePlanCreation(
                    creation,
                    out var outcome)
                || !runtime.TryPublishPlanCreation(creation, outcome))
            {
                return false;
            }

            plan = outcome.Plan;
            return plan != null && plan.Entries.Count > 0;
        }

        private static AntonAwakeningDailyCardService CreateRewardService(
            DailyResetService dailyReset,
            int finalItemId,
            int quantity)
            => new AntonAwakeningDailyCardService(
                dailyReset,
                _ => BuildUpgradableLegacy((finalItemId, 1, quantity)),
                _ => 0);

        private static PvfLib.StackableItemFile BuildUpgradableLegacy(
            params (int ItemId, int Weight, int Count)[] entries)
        {
            var stackable = new PvfLib.StackableItemFile
            {
                StackableType = "[upgradable legacy]",
            };
            foreach (var entry in entries)
            {
                stackable.UpgradableLegacyRewards.Add(
                    new PvfLib.BoosterRewardEntry
                    {
                        RewardKind = "upgradable legacy",
                        ItemId = entry.ItemId,
                        Weight = entry.Weight,
                        Count = entry.Count,
                    });
            }
            return stackable;
        }

        private static DfoServer.GameWorld.SequentialDungeonDefinition
            BuildRewardDefinition(string rewards)
        {
            var catalog = DfoServer.GameWorld
                .SequentialDungeonDefinitionCatalog.Parse(
                    "[sequential dungeon]\n99\n"
                    + "[dungeon index check]\n247\n[/dungeon index check]\n"
                    + "[rewardable dungeon index]\n247\n"
                    + "[/rewardable dungeon index]\n"
                    + "[clear reward item]\n"
                    + rewards
                    + "\n[/clear reward item]\n[/sequential dungeon]",
                    _ => (byte)2);
            if (!catalog.TryGetByGroupKey(99, out var definition))
            {
                throw new InvalidOperationException(
                    "sequential reward fixture failed to parse");
            }
            return definition;
        }

        private static int CountMainItem(InventoryLease lease, int itemId)
        {
            var count = 0;
            lock (lease.SyncRoot)
            {
                for (var slot = InventoryService.MainSlotStart;
                     slot <= InventoryService.MainSlotEnd;
                     slot++)
                {
                    var core = lease.Inventory.GetItem(
                        InventoryListType.Main,
                        slot);
                    if (core?.ItemId == itemId)
                        count += core.Count;
                }
            }
            return count;
        }

        private static void Seed(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@mid", "anton-auto-a");
                command.Parameters.AddWithValue("@name", "anton-auto-c");
                command.ExecuteNonQuery();
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // SQLite may still be releasing a test handle.
            }
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpClient _reader;

            internal ConnectedSession()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    _reader = new TcpClient();
                    var connect = _reader.ConnectAsync(IPAddress.Loopback, port);
                    var writer = listener.AcceptTcpClient();
                    connect.GetAwaiter().GetResult();
                    Session = new EnhancedClientSession(
                        writer,
                        new GamePacketHeader());
                }
                finally
                {
                    listener.Stop();
                }
            }

            internal EnhancedClientSession Session { get; }
            internal int AvailableByteCount => _reader.Available;

            internal List<byte[]> ReadPackets(int minimumCount)
            {
                var packets = new List<byte[]>();
                var stream = _reader.GetStream();
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (packets.Count < minimumCount
                       && DateTime.UtcNow < deadline)
                {
                    var wait = deadline - DateTime.UtcNow;
                    if (!_reader.Client.Poll(
                            (int)Math.Max(1, wait.TotalMilliseconds * 1000),
                            SelectMode.SelectRead))
                    {
                        continue;
                    }

                    var header = ReadExact(stream, 15);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < 15)
                        throw new InvalidOperationException("Invalid packet length.");
                    var packet = new byte[length];
                    Buffer.BlockCopy(header, 0, packet, 0, header.Length);
                    if (length > header.Length)
                    {
                        var body = ReadExact(stream, length - header.Length);
                        Buffer.BlockCopy(
                            body,
                            0,
                            packet,
                            header.Length,
                            body.Length);
                    }
                    packets.Add(packet);
                }
                if (packets.Count < minimumCount)
                {
                    throw new TimeoutException(
                        $"Captured {packets.Count}/{minimumCount} packets.");
                }
                return packets;
            }

            public void Dispose()
            {
                try
                {
                    Session?.TcpClient?.Close();
                }
                catch
                {
                }
                _reader?.Close();
            }

            private static byte[] ReadExact(NetworkStream stream, int count)
            {
                var result = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = stream.Read(result, offset, count - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }
                return result;
            }
        }
    }
}
