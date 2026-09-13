using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AntonAwakeningDailyResetSelfTest
    {
        private static readonly Dictionary<int, int> ExpectedStates =
            new Dictionary<int, int>
            {
                [10157831] = 0,
                [10157832] = 1,
                [10157833] = 2,
                [10157834] = 1,
            };

        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_DAILY_RESET selftest ===");
            var failures = 0;
            VerifyLootCounters(ref failures);
            VerifyRewardPoolParser(ref failures);
            VerifyMalformedRewardPoolsFailClosed(ref failures);
            VerifyRewardDrawPreservesState(ref failures);
            VerifyRewardClaimRollback(ref failures);
            VerifyCrossDayReset(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_DAILY_RESET selftest passed."
                    : $"ANTON_AWAKENING_DAILY_RESET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyLootCounters(ref int failures)
        {
            failures += WithDatabase(
                "loot",
                (database, dailyReset, characterId) =>
                {
                    var localFailures = 0;
                    var guard = new AntonAwakeningDailyLootGuard(dailyReset);
                    Check("243 first loot mark succeeds", guard.TryMarkLootClaimed(characterId, 243), ref localFailures);
                    Check("243 duplicate loot mark is rejected", !guard.TryMarkLootClaimed(characterId, 243), ref localFailures);
                    Check("244 has an independent counter", guard.TryMarkLootClaimed(characterId, 244), ref localFailures);
                    Check("243 reports claimed", guard.HasClaimedLootToday(characterId, 243), ref localFailures);
                    Check("245 reports unclaimed", !guard.HasClaimedLootToday(characterId, 245), ref localFailures);
                    Check("non-Anton dungeons are ignored", !guard.TryMarkLootClaimed(characterId, 999), ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyRewardPoolParser(ref int failures)
        {
            const string rewards =
                "915 10157831 0 10 10157832 1 5 10157833 2 70 10157834 1";
            var candidates = AntonAwakeningDailyCardService.Parse247ClearRewards(
                BuildSequentialRewardConfig(26, 41, rewards));

            Check("key 41 parser finds four entries", candidates != null && candidates.Count == 4, ref failures);
            Check(
                "key 41 parser preserves weights",
                candidates != null
                && candidates.Select(value => value.Weight).SequenceEqual(new[] { 915, 10, 5, 70 }),
                ref failures);
            Check(
                "key 41 parser preserves item IDs",
                candidates != null
                && candidates.Select(value => value.Reward.ItemId)
                    .SequenceEqual(ExpectedStates.Keys),
                ref failures);
            Check(
                "key 41 parser preserves PVF states",
                candidates != null
                && candidates.Select(value => value.Reward.State)
                    .SequenceEqual(ExpectedStates.Values),
                ref failures);
        }

        private static void VerifyMalformedRewardPoolsFailClosed(ref int failures)
        {
            var malformed = new[]
            {
                BuildSequentialRewardConfig(26, 42, "915 10157831 0"),
                BuildSequentialRewardConfig(26, 41, null),
                BuildSequentialRewardConfig(26, 41, "915 10157831"),
                BuildSequentialRewardConfig(26, 41, "bad 10157831 0"),
                BuildSequentialRewardConfig(26, 41, "0 10157831 0"),
                BuildSequentialRewardConfig(26, 41, "915 0 0"),
                BuildSequentialRewardConfig(26, 41, "915 10157831 -1"),
            };

            Check(
                "malformed key 41 definitions fail closed",
                malformed.All(value =>
                    AntonAwakeningDailyCardService.Parse247ClearRewards(value) == null),
                ref failures);
        }

        private static void VerifyRewardDrawPreservesState(ref int failures)
        {
            var candidates = AntonAwakeningDailyCardService.Parse247ClearRewards(
                BuildSequentialRewardConfig(
                    26,
                    41,
                    "1 10157831 0 1 10157832 1 1 10157833 2 1 10157834 1"));
            var service = new AntonAwakeningDailyCardService(null, candidates);
            for (var index = 0; index < 64; index++)
            {
                var drawn = service.TryDrawReward(out var reward);
                Check($"draw {index} succeeds", drawn, ref failures);
                Check(
                    $"draw {index} keeps item/state pair",
                    drawn
                    && ExpectedStates.TryGetValue(reward.ItemId, out var expectedState)
                    && reward.State == expectedState,
                    ref failures);
            }
        }

        private static void VerifyRewardClaimRollback(ref int failures)
        {
            failures += WithDatabase(
                "claim-rollback",
                (database, dailyReset, characterId) =>
                {
                    var localFailures = 0;
                    var service = new AntonAwakeningDailyCardService(
                        dailyReset,
                        Array.Empty<AntonAwakeningRewardCandidate>());
                    using (var connection = database.OpenConnection())
                    using (var transaction = connection.BeginTransaction())
                    {
                        Check(
                            "transactional reward claim succeeds",
                            service.TryClaimReward(connection, transaction, characterId),
                            ref localFailures);
                        transaction.Rollback();
                    }
                    Check(
                        "rolled-back reward claim remains unclaimed",
                        !service.HasClaimedRewardToday(characterId),
                        ref localFailures);
                    return localFailures;
                });
        }

        private static void VerifyCrossDayReset(ref int failures)
        {
            failures += WithDatabase(
                "cross-day",
                (database, dailyReset, characterId) =>
                {
                    var localFailures = 0;
                    var service = new AntonAwakeningDailyCardService(
                        dailyReset,
                        Array.Empty<AntonAwakeningRewardCandidate>());
                    Check("first daily reward claim succeeds", service.TryClaimReward(characterId), ref localFailures);
                    Check("reward reports claimed", service.HasClaimedRewardToday(characterId), ref localFailures);
                    var restartedService = new AntonAwakeningDailyCardService(
                        new DailyResetService(database),
                        Array.Empty<AntonAwakeningRewardCandidate>());
                    Check(
                        "same-day reward claim survives service restart",
                        restartedService.HasClaimedRewardToday(characterId),
                        ref localFailures);
                    using (var connection = database.OpenConnection())
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText =
                            "UPDATE character_daily_reset SET day_id = 0 WHERE character_id = @cid;";
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.ExecuteNonQuery();
                    }
                    var nextDayService = new AntonAwakeningDailyCardService(
                        new DailyResetService(database),
                        Array.Empty<AntonAwakeningRewardCandidate>());
                    Check(
                        "day rollover clears reward claim after service restart",
                        !nextDayService.HasClaimedRewardToday(characterId),
                        ref localFailures);
                    Check(
                        "next-day reward claim succeeds",
                        nextDayService.TryClaimReward(characterId),
                        ref localFailures);
                    return localFailures;
                });
        }

        private static string BuildSequentialRewardConfig(
            int firstKey,
            int targetKey,
            string rewardLine)
        {
            var rewardBlock = rewardLine == null
                ? string.Empty
                : $@"
[clear reward item]
{rewardLine}
[/clear reward item]";
            return $@"
[sequential dungeon]
{firstKey}
[clear reward item]
1 90000000 0
[/clear reward item]
[/sequential dungeon]
[sequential dungeon]
{targetKey}{rewardBlock}
[/sequential dungeon]";
        }

        private static int WithDatabase(
            string suffix,
            Func<IGameDatabase, DailyResetService, int, int> action)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_{suffix}_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                const int accountId = 57800;
                const int characterId = 57801;
                SeedAccount(database, accountId, $"anton-{suffix}-a");
                SeedCharacter(database, characterId, accountId, $"anton-{suffix}-c");
                return action(database, new DailyResetService(database), characterId);
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + "-wal");
                TryDelete(path + "-shm");
            }
        }

        private static void SeedAccount(IGameDatabase database, int accountId, string mid)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@mid", mid);
                command.ExecuteNonQuery();
            }
        }

        private static void SeedCharacter(
            IGameDatabase database,
            int characterId,
            int accountId,
            string name)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@name", name);
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
                // Cleanup is best-effort because SQLite may still be releasing a handle.
            }
        }
    }
}
