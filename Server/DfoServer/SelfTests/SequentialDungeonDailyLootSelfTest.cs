using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;

namespace DfoServer.SelfTests
{
    public static class SequentialDungeonDailyLootSelfTest
    {
        private const int CharacterId = 57901;
        private const int MonsterA = 56675;
        private const int MonsterB = 56678;

        public static int Run()
        {
            Console.WriteLine("=== SEQUENTIAL_DUNGEON_DAILY_LOOT selftest ===");
            var failures = 0;

            VerifyGeneratedResultClaims(ref failures);
            VerifyEmptyAndIndependentKeys(ref failures);
            VerifyDurableAndDailyRollover(ref failures);
            VerifyClaimFailureRollsBack(ref failures);
            VerifyRegisteredDropRollbackIsExact(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "SEQUENTIAL_DUNGEON_DAILY_LOOT selftest passed."
                    : $"SEQUENTIAL_DUNGEON_DAILY_LOOT selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyGeneratedResultClaims(ref int failures)
        {
            failures += WithDatabase(
                "generated",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var rolledBack = new List<DropInfo>();

                    var itemOnly = guard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () => ItemResult(10, 90001),
                        drops => rolledBack.AddRange(drops));
                    var duplicateGenerated = false;
                    var duplicate = guard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () =>
                        {
                            duplicateGenerated = true;
                            return ItemResult(11, 90002);
                        },
                        drops => rolledBack.AddRange(drops));
                    var goldOnly = guard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterB,
                        () => new MonsterDropResult
                        {
                            GoldAmount = 1234,
                            Drops = new List<DropInfo>(),
                        },
                        drops => rolledBack.AddRange(drops));

                    Check(
                        "item-only generation claims the configured monster once",
                        itemOnly.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    Check(
                        "the same monster is suppressed before another generation",
                        duplicate.Drops?.Count == 0
                            && duplicate.GoldAmount == 0
                            && !duplicateGenerated,
                        ref localFailures);
                    Check(
                        "a pure-gold result claims an independent configured monster",
                        goldOnly.GoldAmount == 1234
                            && goldOnly.Drops?.Count == 0
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterB)) == 1,
                        ref localFailures);
                    Check(
                        "successful claims do not invoke rollback",
                        rolledBack.Count == 0,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyEmptyAndIndependentKeys(ref int failures)
        {
            failures += WithDatabase(
                "independent",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var firstDefinition = ParseDefinition(groupKey: 99);
                    var otherDefinition = ParseDefinition(groupKey: 100);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);

                    var empty = guard.GenerateAndMark(
                        CharacterId,
                        firstDefinition,
                        MonsterA,
                        EmptyResult,
                        _ => throw new InvalidOperationException(
                            "empty results must not roll back"));
                    var emptyDidNotClaim = dailyReset.GetCounter(
                        CharacterId,
                        SequentialDungeonDailyLootGuard.BuildCounterKey(
                            firstDefinition.GroupKey,
                            MonsterA)) == 0;
                    var multiItem = guard.GenerateAndMark(
                        CharacterId,
                        firstDefinition,
                        MonsterA,
                        () => new MonsterDropResult
                        {
                            Drops = new List<DropInfo>
                            {
                                DropInfo.CreateItem(20, 90011, 1),
                                DropInfo.CreateItem(21, 90012, 2),
                            },
                        },
                        _ => { });
                    var mixed = guard.GenerateAndMark(
                        CharacterId,
                        firstDefinition,
                        MonsterB,
                        () => new MonsterDropResult
                        {
                            GoldAmount = 500,
                            Drops = new List<DropInfo>
                            {
                                DropInfo.CreateItem(22, 90013, 1),
                            },
                        },
                        _ => { });
                    var otherGroup = guard.GenerateAndMark(
                        CharacterId,
                        otherDefinition,
                        MonsterA,
                        () => ItemResult(23, 90014),
                        _ => { });
                    var nonConfigured = guard.GenerateAndMark(
                        CharacterId,
                        firstDefinition,
                        monsterId: 99999,
                        generate: () => ItemResult(24, 90015),
                        rollback: _ => { });

                    Check(
                        "an empty roll remains empty and does not claim",
                        empty.Drops?.Count == 0
                            && emptyDidNotClaim,
                        ref localFailures);
                    Check(
                        "multiple items in one death consume only one claim",
                        multiItem.Drops?.Count == 2
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    firstDefinition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    Check(
                        "mixed item and gold generation consumes only one claim",
                        mixed.Drops?.Count == 1
                            && mixed.GoldAmount == 500
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    firstDefinition.GroupKey,
                                    MonsterB)) == 1,
                        ref localFailures);
                    Check(
                        "the same monster in another sequential group is independent",
                        otherGroup.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    otherDefinition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    Check(
                        "a non-configured monster generates without creating a counter",
                        nonConfigured.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    firstDefinition.GroupKey,
                                    99999)) == 0,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyDurableAndDailyRollover(ref int failures)
        {
            failures += WithDatabase(
                "durable",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var firstGuard = new SequentialDungeonDailyLootGuard(
                        dailyReset);
                    firstGuard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () => ItemResult(30, 90101),
                        _ => { });

                    var recreatedGenerated = false;
                    var recreatedGuard = new SequentialDungeonDailyLootGuard(
                        new DailyResetService(database));
                    var afterReentry = recreatedGuard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () =>
                        {
                            recreatedGenerated = true;
                            return ItemResult(31, 90102);
                        },
                        _ => { });
                    Check(
                        "a generated drop stays claimed after guard reconstruction",
                        afterReentry.Drops?.Count == 0
                            && !recreatedGenerated,
                        ref localFailures);

                    ForcePreviousGameDay(database);
                    var afterRollover = recreatedGuard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () => ItemResult(32, 90103),
                        _ => { });
                    Check(
                        "the Beijing 06:00 game-day rollover restores eligibility",
                        afterRollover.Drops?.Count == 1
                            && dailyReset.GetCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA)) == 1,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyClaimFailureRollsBack(ref int failures)
        {
            failures += WithDatabase(
                "claim-race",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var rolledBack = new List<DropInfo>();
                    var raced = guard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () =>
                        {
                            dailyReset.TryIncrementCounter(
                                CharacterId,
                                SequentialDungeonDailyLootGuard.BuildCounterKey(
                                    definition.GroupKey,
                                    MonsterA),
                                cap: 1,
                                period: DailyResetService.PeriodDay);
                            return ItemResult(40, 90201);
                        },
                        drops => rolledBack.AddRange(drops));
                    Check(
                        "a concurrently consumed cap rolls back the generated batch",
                        raced.Drops?.Count == 0
                            && raced.GoldAmount == 0
                            && rolledBack.Count == 1
                            && rolledBack[0].SceneSlot == 40,
                        ref localFailures);
                    return localFailures;
                });

            failures += WithDatabase(
                "claim-exception",
                (database, dailyReset) =>
                {
                    var localFailures = 0;
                    var definition = ParseDefinition(groupKey: 99);
                    InjectCounterFailure(database);
                    var guard = new SequentialDungeonDailyLootGuard(dailyReset);
                    var rolledBack = new List<DropInfo>();
                    var failed = guard.GenerateAndMark(
                        CharacterId,
                        definition,
                        MonsterA,
                        () => ItemResult(41, 90202),
                        drops => rolledBack.AddRange(drops));
                    Check(
                        "a counter persistence exception rolls back and fails closed",
                        failed.Drops?.Count == 0
                            && failed.GoldAmount == 0
                            && rolledBack.Count == 1
                            && rolledBack[0].SceneSlot == 41,
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyRegisteredDropRollbackIsExact(
            ref int failures)
        {
            var run = new DungeonRun();
            var service = new DropService();
            var exact = DropInfo.CreateItem(50, 90301, 2);
            exact.DropGroupId = 700;
            var changedCount = DropInfo.CreateItem(51, 90302, 1);
            changedCount.DropGroupId = 701;
            var changedGroup = DropInfo.CreateItem(52, 90303, 1);
            changedGroup.DropGroupId = 702;
            var changedTemplate = DropInfo.CreateItem(53, 90304, 1);
            changedTemplate.DropGroupId = 703;

            run.Drops[50] = exact;
            var currentChangedCount = changedCount;
            currentChangedCount.StackCount = 2;
            run.Drops[51] = currentChangedCount;
            var currentChangedGroup = changedGroup;
            currentChangedGroup.DropGroupId++;
            run.Drops[52] = currentChangedGroup;
            var currentChangedTemplate = changedTemplate;
            currentChangedTemplate.TemplateId++;
            run.Drops[53] = currentChangedTemplate;
            run.SceneSlotCounter = 53;

            service.RollbackRegistered(
                run,
                new[]
                {
                    exact,
                    changedCount,
                    changedGroup,
                    changedTemplate,
                });
            Check(
                "rollback removes only an exact slot/group/template/count match",
                !run.Drops.ContainsKey(50)
                    && run.Drops.ContainsKey(51)
                    && run.Drops.ContainsKey(52)
                    && run.Drops.ContainsKey(53),
                ref failures);
            Check(
                "rollback never rewinds the scene slot counter",
                run.SceneSlotCounter == 53,
                ref failures);
        }

        private static SequentialDungeonDefinition ParseDefinition(
            int groupKey)
        {
            var catalog = SequentialDungeonDefinitionCatalog.Parse(
                $@"
[sequential dungeon]
{groupKey}
[dungeon index check]
243 244
[/dungeon index check]
[monster index check]
{MonsterA} {MonsterB}
[/monster index check]
[show individual process]
[/show individual process]
[/sequential dungeon]",
                _ => (byte)2);
            if (!catalog.TryGetByGroupKey(groupKey, out var definition))
            {
                throw new InvalidOperationException(
                    $"failed to parse sequential loot group {groupKey}");
            }
            return definition;
        }

        private static MonsterDropResult EmptyResult() =>
            new MonsterDropResult
            {
                Drops = new List<DropInfo>(),
            };

        private static MonsterDropResult ItemResult(
            ushort sceneSlot,
            int itemId) =>
            new MonsterDropResult
            {
                Drops = new List<DropInfo>
                {
                    DropInfo.CreateItem(sceneSlot, itemId, 1),
                },
            };

        private static int WithDatabase(
            string suffix,
            Func<IGameDatabase, DailyResetService, int> action)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_sequential_loot_{suffix}_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                SeedCharacter(database, suffix);
                return action(database, new DailyResetService(database));
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void SeedCharacter(IGameDatabase database, string suffix)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (57900, @mid, '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, 57900, @name, 0);";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@mid", "sequential-loot-" + suffix);
                command.Parameters.AddWithValue("@name", "loot-" + suffix);
                command.ExecuteNonQuery();
            }
        }

        private static void ForcePreviousGameDay(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE character_daily_reset
SET day_id = 0
WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void InjectCounterFailure(IGameDatabase database)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
CREATE TRIGGER fail_sequential_monster_counter
BEFORE INSERT ON character_daily_counters
WHEN NEW.counter_key LIKE 'sequential_monster_drop_v1:%'
BEGIN
    SELECT RAISE(ABORT, 'injected sequential monster counter failure');
END;";
                command.ExecuteNonQuery();
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
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
            }
        }
    }
}
