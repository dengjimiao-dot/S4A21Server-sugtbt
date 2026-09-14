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

            var rewards = new AntonAwakeningDailyCardService(
                null,
                new[]
                {
                    new AntonAwakeningRewardCandidate(
                        1,
                        new AntonAwakeningRewardDefinition(10157834, 1)),
                });
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

                var daily = new AntonAwakeningDailyCardService(
                    new DailyResetService(database),
                    new[]
                    {
                        new AntonAwakeningRewardCandidate(
                            1,
                            new AntonAwakeningRewardDefinition(10157831, 0)),
                    });
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
                    capture.ReadPackets(2);
                    Check(
                        "Anton inventory and daily claim remain absent before deadline",
                        CountMainItem(lease, 10157831) == 0
                        && !daily.HasClaimedRewardToday(characterId),
                        ref failures);

                    ClockService.Instance.CheckOnce(
                        DateTime.UtcNow.AddSeconds(1));
                    var grantDeadline = DateTime.UtcNow.AddSeconds(3);
                    while (!daily.HasClaimedRewardToday(characterId)
                           && DateTime.UtcNow < grantDeadline)
                    {
                        Thread.Sleep(25);
                    }
                    var grantedCount = CountMainItem(lease, 10157831);
                    Check(
                        "Anton timer commits the frozen reward and daily claim once",
                        daily.HasClaimedRewardToday(characterId)
                        && grantedCount == 1
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
                        CountMainItem(lease, 10157831) == grantedCount,
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

            var rewards = new AntonAwakeningDailyCardService(
                null,
                new[]
                {
                    new AntonAwakeningRewardCandidate(
                        1,
                        new AntonAwakeningRewardDefinition(10157834, 1)),
                });
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
            var candidates = new[]
            {
                new AntonAwakeningRewardCandidate(
                    1,
                    new AntonAwakeningRewardDefinition(10157834, 1)),
                new AntonAwakeningRewardCandidate(
                    1,
                    new AntonAwakeningRewardDefinition(10157833, 2)),
            };
            var rolls = new Queue<int>(new[] { 0, 1 });
            var drawCalls = 0;
            var rewards = new AntonAwakeningDailyCardService(
                null,
                candidates,
                maximum =>
                {
                    drawCalls++;
                    var value = rolls.Dequeue();
                    return value < maximum ? value : maximum - 1;
                });
            var runtime = new AntonAwakeningRewardRuntime();
            var eventId = Guid.NewGuid();

            var first = runtime.TryGetOrCreatePlan(
                eventId,
                roster,
                rewards,
                runA.Instance.SequentialDefinition,
                runA.DungeonId,
                out var firstPlan);
            var second = runtime.TryGetOrCreatePlan(
                eventId,
                new[] { roster[0] },
                rewards,
                runA.Instance.SequentialDefinition,
                runA.DungeonId,
                out var secondPlan);
            Check(
                "same clear event reuses one immutable participant plan",
                first
                && second
                && ReferenceEquals(firstPlan, secondPlan)
                && firstPlan.Entries.Count == 2
                && drawCalls == 2,
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
                && firstPlan.Entries[1].Reward.ItemId == 10157833
                && firstPlan.Entries[1].Reward.State == 2,
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
                    Array.Empty<AntonAwakeningRewardCandidate>());
                var grants = new AntonAwakeningRewardGrantService(daily);

                var failed = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(int.MaxValue, 2));
                Check(
                    "failed inventory insertion rolls back daily claim",
                    failed.Outcome == AntonAwakeningRewardGrantOutcome.Failed
                    && !daily.HasClaimedRewardToday(characterId),
                    ref failures);

                var granted = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(10157831, 0));
                var countAfterGrant = CountMainItem(lease, 10157831);
                var duplicate = grants.TryGrant(
                    lease,
                    new AntonAwakeningRewardDefinition(10157831, 0));
                Check(
                    "reward and daily claim commit in one transaction",
                    granted.Outcome == AntonAwakeningRewardGrantOutcome.Granted
                    && daily.HasClaimedRewardToday(characterId)
                    && countAfterGrant == 1,
                    ref failures);
                Check(
                    "duplicate clear becomes committed no-reward",
                    duplicate.Outcome
                        == AntonAwakeningRewardGrantOutcome.AlreadyClaimed
                    && CountMainItem(lease, 10157831) == countAfterGrant,
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
