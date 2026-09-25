using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Tournament;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class A21SpecialDungeonProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_SPECIAL_DUNGEON_PROTOCOL selftest ===");
            var failures = 0;

            VerifyHuntOnlyBossEntrance(ref failures);
            VerifyTournamentPayloads(ref failures);
            VerifyBloodAltarPayloads(ref failures);
            VerifyBossDieCheckGate(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_SPECIAL_DUNGEON_PROTOCOL selftest passed."
                    : $"A21_SPECIAL_DUNGEON_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyTournamentPayloads(ref int failures)
        {
            var candidates = new List<TournamentActorDefinition>();
            for (var index = 0; index < 15; index++)
            {
                candidates.Add(new TournamentActorDefinition(
                    partyCount: 1,
                    TournamentActorKind.Monster,
                    code: 56000 + index,
                    strength: 100 + index,
                    name: string.Empty,
                    level: 70,
                    actorType: 0));
            }

            var definition = new TournamentDungeonDefinition(
                dungeonId: 120,
                mapId: 17100,
                basicLevel: 70,
                partyLimit: 1,
                coinLimit: 3,
                roundFatigue: 0,
                clearRewardGoldRate: 1f,
                experienceByRound: null,
                resultCards: null,
                rewardItemRates: Array.Empty<TournamentRewardItemRateDefinition>(),
                candidates,
                startAreas: Array.Empty<TournamentStartAreaDefinition>(),
                entryItems: Array.Empty<TournamentEntryItemDefinition>());
            if (!TournamentDungeonRuntimeFactory.TryCreate(
                    definition,
                    partyCount: 1,
                    _ => 0,
                    out var runtime,
                    out var failureReason))
            {
                Check(
                    $"tournament runtime can be created: {failureReason}",
                    false,
                    ref failures);
                return;
            }

            var info = TournamentPacketBuilder.BuildTournamentInfo(
                runtime,
                difficulty: 2,
                firstMonsterSequence: 0x2711);
            Check(
                "TOURNAMENT_INFO uses the captured 260-byte body",
                info.Length == 260,
                ref failures);
            Check(
                "TOURNAMENT_INFO starts with u32 dungeon id, difficulty and party limit",
                ReadUInt32(info, 0) == 120
                && info[4] == 2
                && info[5] == 1,
                ref failures);
            Check(
                "TOURNAMENT_INFO path actor sequence remains aligned after the bracket",
                info[224] == 1
                && ReadUInt16(info, 225) == 0x2711,
                ref failures);

            const uint seed = 0xDD23D90E;
            var map = TournamentPacketBuilder.BuildTournamentMapInfo(
                x: 0,
                y: 0,
                seed,
                mapId: 17100,
                revisit: false);
            Check(
                "TOURNAMENT_MAP_INFO uses the captured 15-byte body",
                map.Length == 15,
                ref failures);
            Check(
                "TOURNAMENT_MAP_INFO writes u32 map id and explicit tail fields",
                ReadUInt32(map, 2) == seed
                && map[6] == 0
                && map[7] == 1
                && ReadUInt32(map, 8) == 17100
                && map[12] == 0
                && map[13] == 0
                && map[14] == 0,
                ref failures);
        }

        private static void VerifyBloodAltarPayloads(ref int failures)
        {
            var endlessInfo = BloodAltarPacketBuilder.BuildInfo(
                11006,
                BloodAltarDungeonKind.Endless);
            var ultimateInfo = BloodAltarPacketBuilder.BuildInfo(
                11007,
                BloodAltarDungeonKind.Ultimate);
            Check(
                "BLOOD_INFO keeps the captured 12-byte body for both altar kinds",
                endlessInfo.Length == 12 && ultimateInfo.Length == 12,
                ref failures);
            Check(
                "BLOOD_INFO maps the PVF altar kind to the captured mode word",
                ReadUInt32(endlessInfo, 0) == 11006
                && ReadUInt16(endlessInfo, 4) == 0
                && ReadUInt16(endlessInfo, 6) == 2
                && ReadUInt32(endlessInfo, 8) == 0
                && ReadUInt32(ultimateInfo, 0) == 11007
                && ReadUInt16(ultimateInfo, 4) == 0
                && ReadUInt16(ultimateInfo, 6) == 0
                && ReadUInt32(ultimateInfo, 8) == 0,
                ref failures);

            var firstMap = BloodAltarPacketBuilder.BuildStartMap(
                x: 0,
                y: 0,
                seed: 0x028080C0,
                mapId: 16348);
            var movedMap = BloodAltarPacketBuilder.BuildStartMap(
                x: 1,
                y: 0,
                seed: 0x0002084D,
                mapId: 16353);
            Check(
                "START_BLOOD_MAP remains 15 bytes on entry and map transitions",
                firstMap.Length == 15 && movedMap.Length == 15,
                ref failures);
            Check(
                "START_BLOOD_MAP writes the captured mode, u32 map id and zero tails",
                movedMap[0] == 1
                && movedMap[1] == 0
                && ReadUInt32(movedMap, 2) == 0x0002084D
                && movedMap[6] == 0
                && movedMap[7] == 1
                && ReadUInt32(movedMap, 8) == 16353
                && movedMap[12] == 0
                && movedMap[13] == 0
                && movedMap[14] == 0,
                ref failures);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
            => BitConverter.ToUInt16(data, offset);

        private static uint ReadUInt32(byte[] data, int offset)
            => BitConverter.ToUInt32(data, offset);

        private static void VerifyHuntOnlyBossEntrance(ref int failures)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH")))
                return;

            var application = new SpecialDungeonMechanismApplicationService();
            using var tcpClient = new TcpClient();
            var session = new EnhancedClientSession(tcpClient, new GamePacketHeader());
            session.Player.CharacterId = 10041;
            foreach (short dungeonId in new short[] { 35, 37 })
            {
                var dungeon = Dungeon.GetDungeonFile(dungeonId);
                var expectedCount = dungeonId == 35 ? 4 : 1;
                for (var mazeIndex = 0; mazeIndex < dungeon.Mazes.Count; mazeIndex++)
                {
                    var maze = dungeon.Mazes[mazeIndex];
                    var run = new DungeonRun(dungeonId, 0)
                    {
                        MazeIndex = mazeIndex,
                        BossMapPos = maze.BossMap,
                    };
                    session.Player.CurrentRun = run;
                    SpecialDungeonRunCoordinator.ConfigureSelection(
                        run, maze, maze.BossMap, Array.Empty<DfoServer.Game.Quests.ActiveQuest>());
                    var label = $"dungeon={dungeonId} maze={mazeIndex}";
                    var targets = run.BossEntranceConditionTargets;
                    Check($"{label} assigns all hunt-only targets without a summoned boss",
                        targets.Count == expectedCount
                        && run.Mechanisms.HasBossEntranceCondition
                        && !run.HasBossEntranceConditionalSummon, ref failures);
                    if (targets.Count != expectedCount)
                        continue;

                    var member = new DungeonRun(run.Instance, run.RunId + 1, 1,
                        DungeonRunState.Active);
                    SpecialDungeonRunCoordinator.CloneSelectionState(run, member);
                    Check($"{label} party selection retains the same target rooms",
                        member.BossEntranceConditionTargets.Select(t => (t.MonsterCode, t.X, t.Y))
                            .SequenceEqual(targets.Select(t => (t.MonsterCode, t.X, t.Y))),
                        ref failures);

                    run.RoomKey = new RoomKey(255, 255, -1);
                    Check($"{label} a kill outside the assigned room keeps the gate closed",
                        application.ApplyMonsterKilled(run, targets[0].MonsterCode, 0).Count == 0
                        && !run.BossEntranceConditionComplete, ref failures);

                    for (var index = 0; index < targets.Count; index++)
                    {
                        var target = targets[index];
                        var room = Dungeon.GetDungeonMapMonsterSummaryInformation(
                            dungeonId, target.X, target.Y, mazeIndex);
                        SpecialDungeonRunCoordinator.AppendStartMapActors(session, run, room);
                        Check($"{label} target {target.MonsterCode} enters START_MAP as a blocking actor",
                            room.Monsters.Count(m => m.Code == target.MonsterCode
                                && m.IsBlocking && m.Flag0 == 0) == 1, ref failures);

                        run.RoomKey = new RoomKey(target.X, target.Y, -1);
                        var effects = application.ApplyMonsterKilled(run, target.MonsterCode, 0);
                        var finalTarget = index == targets.Count - 1;
                        Check($"{label} target {index + 1}/{targets.Count} opens the gate only at completion",
                            target.Completed
                            && run.BossEntranceConditionComplete == finalTarget
                            && effects.Count(e => e.Kind == SpecialDungeonEffectKind.PassGate)
                                == (finalTarget ? 1 : 0), ref failures);
                        Check($"{label} repeated target death preserves the completed result",
                            application.ApplyMonsterKilled(run, target.MonsterCode, 0).Count == 0,
                            ref failures);
                    }
                    Check($"{label} hunt-only completion retains ordinary boss handling",
                        !run.HasBossEntranceConditionalSummon && !run.ConditionalBossSpawned,
                        ref failures);
                }
            }
            var body = SpecialDungeonNotificationBuilder.BuildCompleteConditionPassGateTrigger();
            Check("A21 gate condition notification retains the consumed i32 and u8 body",
                body.Length == 5 && BitConverter.ToInt32(body, 0) == 0 && body[4] == 0,
                ref failures);
        }

        private static void VerifyBossDieCheckGate(ref int failures)
        {
            using (var tcpClient = new TcpClient())
            {
                var session = new EnhancedClientSession(
                    tcpClient,
                    new GamePacketHeader());
                session.Player.CharacterId = 10040;

                var run = new DungeonRun(
                    new DungeonInstance(2010, 0),
                    runId: 900,
                    runGeneration: 1,
                    DungeonRunState.Active)
                {
                    Phase = DungeonRunPhase.InProgress,
                    BossEntranceConditionTargets =
                        new List<BossEntranceConditionTargetState>
                        {
                            new BossEntranceConditionTargetState
                            {
                                MonsterCode = 50001,
                                Completed = true,
                            },
                        },
                    BossEntranceConditionalSummonCodes =
                        new List<int> { 69264 },
                    BossEntranceConditionComplete = true,
                };
                session.Player.CurrentRun = run;

                var request = new BossDieCheckRequest(
                    userId: 7,
                    bossSequence: SpecialDungeonNotifier.BossSummonRuntimeKey);

                var reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check rejects before the conditional boss is spawned",
                    !reported.ShouldClearDungeon,
                    ref failures);

                Check(
                    "conditional boss spawn is registered once at instance scope",
                    run.Instance.Mechanisms.TryRegisterConditionalBossSpawn(69264)
                    && !run.Instance.Mechanisms.TryRegisterConditionalBossSpawn(69265),
                    ref failures);

                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check clears for a non-summoner from the shared spawn fact",
                    reported.ShouldClearDungeon && reported.BossCode == 69264,
                    ref failures);

                // A stale participant-local projection must not override the
                // instance-level BossCode selected by the accepted summon.
                run.ConditionalBossSpawned = true;
                run.ConditionalBossCode = 69265;
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "participant-local boss code cannot override the shared spawn code",
                    reported.ShouldClearDungeon && reported.BossCode == 69264,
                    ref failures);

                run.BossEntranceConditionComplete = false;
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check rejects while the entrance condition " +
                    "is incomplete",
                    !reported.ShouldClearDungeon,
                    ref failures);

                run.BossEntranceConditionComplete = true;
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    new BossDieCheckRequest(userId: 7, bossSequence: 0x1234));
                Check(
                    "boss die check rejects a non-summon boss sequence",
                    !reported.ShouldClearDungeon,
                    ref failures);

                run.Instance.Mechanisms.ResetConditionalBossSpawn();
                run.BossEntranceConditionalSummonCodes.Clear();
                reported = DungeonMechanismCoordinator.OnBossDieCheck(
                    session,
                    run,
                    request);
                Check(
                    "boss die check rejects after the shared spawn fact is reset",
                    !reported.ShouldClearDungeon,
                    ref failures);
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
