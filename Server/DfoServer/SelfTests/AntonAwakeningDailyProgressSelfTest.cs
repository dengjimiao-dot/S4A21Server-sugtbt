using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningDailyProgressSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_DAILY_PROGRESS selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_progress_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(
                    tempDbPath,
                    ServerPaths.SchemaFilePath);
                const int accountA = 61000;
                const int accountB = 61001;
                const int characterA = 61010;
                const int characterB = 61011;
                const int stateCharacter = 61012;
                const int incompleteCharacter = 61013;
                SeedAccount(database, accountA, "anton-progress-a");
                SeedAccount(database, accountB, "anton-progress-b");
                SeedCharacter(database, characterA, accountA, "anton-progress-a1");
                SeedCharacter(database, stateCharacter, accountA, "anton-progress-a2");
                SeedCharacter(database, incompleteCharacter, accountA, "anton-progress-a3");
                SeedCharacter(database, characterB, accountB, "anton-progress-b1");

                var beforeUtc = new DateTime(
                    2026, 9, 11, 21, 59, 59, DateTimeKind.Utc);
                var boundaryUtc = new DateTime(
                    2026, 9, 11, 22, 0, 0, DateTimeKind.Utc);
                var dailyReset = new DailyResetService(database);
                var repository = new AntonAwakeningDailyProgressRepository(
                    database,
                    dailyReset);

                repository.EnsureCurrentDayAndLoad(characterA, beforeUtc);
                foreach (var dungeonId in new[]
                    { 225, 231, 243, 244, 245, 246, 247 })
                {
                    SeedPermission(database, characterA, dungeonId, 3);
                }
                SeedPermission(database, characterB, 243, 3);

                var beforeRows = repository.EnsureCurrentDayAndLoad(
                    characterA,
                    beforeUtc);
                Check(
                    "same-day load preserves all five awakening rows",
                    beforeRows.Count == 5,
                    ref failures);

                var afterRows = repository.EnsureCurrentDayAndLoad(
                    characterA,
                    boundaryUtc);
                Check(
                    "06:00 rollover removes only the current character's 243-247 rows",
                    afterRows.Count == 0
                    && PermissionExists(database, characterA, 225)
                    && PermissionExists(database, characterA, 231)
                    && PermissionExists(database, characterB, 243),
                    ref failures);

                SeedCounter(
                    database,
                    characterB,
                    AntonAwakeningDailyProgressRepository.MarkerKey,
                    DailyResetService.PeriodWeek,
                    0);
                var markerFailureRolledBack = false;
                try
                {
                    repository.EnsureCurrentDayAndLoad(characterB, boundaryUtc);
                }
                catch (InvalidOperationException)
                {
                    markerFailureRolledBack = true;
                }
                Check(
                    "marker failure rolls back permission deletion",
                    markerFailureRolledBack
                    && PermissionExists(database, characterB, 243),
                    ref failures);
                Check(
                    "rollover installs one current-day marker",
                    ReadMarker(database, characterA) == 1
                    && ReadDayId(database, characterA)
                        == DailyResetService.TodayId(boundaryUtc),
                    ref failures);

                var anchoredNow = boundaryUtc.AddSeconds(1);
                var service = new AntonAwakeningDailyProgressService(
                    repository,
                    () => anchoredNow);
                var expected = new[]
                {
                    (DungeonId: 243, Progress: (byte)1, Mask: 0x01),
                    (DungeonId: 244, Progress: (byte)2, Mask: 0x03),
                    (DungeonId: 245, Progress: (byte)3, Mask: 0x07),
                    (DungeonId: 246, Progress: (byte)4, Mask: 0x0F),
                };
                foreach (var item in expected)
                {
                    var applied = service.TryApplyClear(
                        characterA,
                        item.DungeonId,
                        out var result);
                    Check(
                        $"clear {item.DungeonId} projects progress and route mask",
                        applied
                        && result.State.Sequence.ConfigKey == 41
                        && result.State.ProgressIndex == item.Progress
                        && result.State.RouteMask == item.Mask,
                        ref failures);
                }
                Check(
                    "clear 247 keeps all prerequisites and advances progress to five",
                    service.TryApplyClear(characterA, 247, out var finalResult)
                    && finalResult.State.ProgressIndex == 5
                    && finalResult.State.RouteMask == 0x0F,
                    ref failures);

                var reconstructed = new AntonAwakeningDailyProgressService(
                    new AntonAwakeningDailyProgressRepository(
                        database,
                        new DailyResetService(database)),
                    () => anchoredNow.AddMinutes(1));
                Check(
                    "same-day progress survives service reconstruction",
                    reconstructed.TryRestore(characterA, 41, out var restored)
                    && restored.ProgressIndex == 5
                    && restored.RouteMask == 0x0F,
                    ref failures);

                repository.EnsureCurrentDayAndLoad(stateCharacter, anchoredNow);
                var completedState = ResolveCompletedState(245);
                Check(
                    "current PVF completed state for awakening difficulty is three",
                    completedState == 3,
                    ref failures);
                RecordState(repository, stateCharacter, 245, 1, anchoredNow);
                Check(
                    "state one does not satisfy a route bit",
                    service.TryRestore(stateCharacter, 41, out var stateOne)
                    && stateOne.RouteMask == 0,
                    ref failures);
                RecordState(repository, stateCharacter, 245, 2, anchoredNow);
                Check(
                    "state two does not satisfy a route bit",
                    service.TryRestore(stateCharacter, 41, out var stateTwo)
                    && stateTwo.RouteMask == 0,
                    ref failures);
                RecordState(
                    repository,
                    stateCharacter,
                    245,
                    completedState,
                    anchoredNow);
                Check(
                    "completed state sets only dungeon 245's route bit",
                    service.TryRestore(stateCharacter, 41, out var stateThree)
                    && stateThree.RouteMask == 0x04,
                    ref failures);

                repository.EnsureCurrentDayAndLoad(
                    incompleteCharacter,
                    anchoredNow);
                foreach (var dungeonId in new[] { 243, 244, 246 })
                {
                    RecordState(
                        repository,
                        incompleteCharacter,
                        dungeonId,
                        ResolveCompletedState(dungeonId),
                        anchoredNow);
                }
                var leaderDecision = service.EvaluateAdmission(characterA, 247);
                var followerDecision = service.EvaluateAdmission(
                    incompleteCharacter,
                    247);
                Check(
                    "247 admission is character-scoped and lists the exact missing prerequisite",
                    leaderDecision.Allowed
                    && !followerDecision.Allowed
                    && followerDecision.MissingDungeonIds.SequenceEqual(
                        new[] { 245 }),
                    ref failures);
                Check(
                    "non-247 admission is a no-op without a character lookup",
                    service.EvaluateAdmission(0, 246).Allowed,
                    ref failures);

                var invalidRejected = false;
                try
                {
                    RecordState(repository, characterA, 242, 3, anchoredNow);
                }
                catch (ArgumentException)
                {
                    invalidRejected = true;
                }
                Check(
                    "out-of-scope updates fail before mutating permission rows",
                    invalidRejected
                    && !PermissionExists(database, characterA, 242),
                    ref failures);


                const int rollbackCharacter = 61014;
                SeedCharacter(
                    database,
                    rollbackCharacter,
                    accountA,
                    "anton-progress-a4");
                repository.EnsureCurrentDayAndLoad(
                    rollbackCharacter,
                    anchoredNow);
                CreateFailingPermissionTrigger(database);
                var mutationRolledBack = false;
                try
                {
                    repository.RecordClearAndLoad(
                        rollbackCharacter,
                        new[]
                        {
                            new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = 243,
                                ClearState = ResolveCompletedState(243),
                            },
                            new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = 245,
                                ClearState = ResolveCompletedState(245),
                            },
                        },
                        anchoredNow,
                        out _);
                }
                catch (SqliteException)
                {
                    mutationRolledBack = true;
                }
                finally
                {
                    DropFailingPermissionTrigger(database);
                }
                Check(
                    "multi-row clear mutation rolls back atomically on SQL failure",
                    mutationRolledBack
                    && !PermissionExists(database, rollbackCharacter, 243)
                    && !PermissionExists(database, rollbackCharacter, 245),
                    ref failures);

                Check(
                    "key 41 and key 28 retain their PVF-derived identities",
                    AntonNormalConquest.TryGetSequenceByKey(41, out var awakening)
                    && awakening.DungeonIds.SequenceEqual(
                        new[] { 243, 244, 245, 246, 247 })
                    && AntonNormalConquest.TryGetSequenceByKey(28, out var normal)
                    && normal.DungeonIds.Take(5).SequenceEqual(
                        new[] { 225, 226, 228, 229, 231 })
                    && AntonNormalConquest.TryResolveClearPlan(
                        225,
                        out var normalPlan)
                    && normalPlan.Sequence.ConfigKey == 28,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[ANTON_AWAKENING_DAILY_PROGRESS] EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }

            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_DAILY_PROGRESS selftest passed."
                    : $"ANTON_AWAKENING_DAILY_PROGRESS selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte ResolveCompletedState(int dungeonId)
        {
            if (!AntonNormalConquest.TryGetSequenceByKey(41, out var sequence)
                || !AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    sequence.Difficulty,
                    out var state))
            {
                throw new InvalidOperationException(
                    $"Unable to resolve completed state for dungeon {dungeonId}.");
            }
            return state;
        }

        private static void RecordState(
            AntonAwakeningDailyProgressRepository repository,
            int characterId,
            int dungeonId,
            byte clearState,
            DateTime utcNow)
        {
            repository.RecordClearAndLoad(
                characterId,
                new[]
                {
                    new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = checked((ushort)dungeonId),
                        ClearState = clearState,
                    },
                },
                utcNow,
                out _);
        }

        private static void SeedAccount(
            GameDatabase database,
            int accountId,
            string mid)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", mid);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void SeedCharacter(
            GameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void SeedPermission(
            GameDatabase database,
            int characterId,
            int dungeonId,
            byte clearState)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_dungeon_permissions
    (character_id, sort_order, dungeon_id, clear_state)
VALUES
    (@cid,
     (SELECT COALESCE(MAX(sort_order), 0) + 1
      FROM character_dungeon_permissions
      WHERE character_id = @cid),
     @did,
     @state);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.Parameters.AddWithValue("@state", clearState);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static bool PermissionExists(
            GameDatabase database,
            int characterId,
            int dungeonId)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM character_dungeon_permissions
WHERE character_id = @cid AND dungeon_id = @did;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    return Convert.ToInt32(command.ExecuteScalar()) > 0;
                }
            });
        }

        private static void SeedCounter(
            GameDatabase database,
            int characterId,
            string key,
            string period,
            long value)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO character_daily_counters
    (character_id, counter_key, period, value)
VALUES (@cid, @key, @period, @value);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@key", key);
                    command.Parameters.AddWithValue("@period", period);
                    command.Parameters.AddWithValue("@value", value);
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void CreateFailingPermissionTrigger(
            GameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
CREATE TRIGGER selftest_fail_anton_permission
BEFORE INSERT ON character_dungeon_permissions
WHEN NEW.dungeon_id = 245
BEGIN
    SELECT RAISE(ABORT, 'selftest permission failure');
END;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static void DropFailingPermissionTrigger(GameDatabase database)
        {
            database.Write((connection, transaction) =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText =
                        "DROP TRIGGER IF EXISTS selftest_fail_anton_permission;";
                    command.ExecuteNonQuery();
                }
            });
        }

        private static long ReadMarker(GameDatabase database, int characterId)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT value
FROM character_daily_counters
WHERE character_id = @cid
  AND counter_key = @key
  AND period = 'day';";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue(
                        "@key",
                        AntonAwakeningDailyProgressRepository.MarkerKey);
                    return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
                }
            });
        }

        private static int ReadDayId(GameDatabase database, int characterId)
        {
            return database.Read(connection =>
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT day_id
FROM character_daily_reset
WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(command.ExecuteScalar() ?? 0);
                }
            });
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

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
