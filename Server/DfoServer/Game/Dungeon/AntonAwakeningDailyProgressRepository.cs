using DfoServer.Game.DailyReset;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class AntonAwakeningDailyProgressRepository
    {
        internal const string MarkerKey =
            "anton_awakening_progress_initialized";

        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;

        internal AntonAwakeningDailyProgressRepository(
            IGameDatabase database,
            DailyResetService dailyReset)
        {
            _database = database
                ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset
                ?? throw new ArgumentNullException(nameof(dailyReset));
        }

        internal List<DungeonPermissionEntrySnapshot>
            EnsureCurrentDayAndLoad(int characterId, DateTime utcNow)
        {
            ValidateCharacterId(characterId);
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                EnsureCurrentDay(
                    connection,
                    transaction,
                    characterId,
                    utcNow);
                var snapshot = Load(
                    connection,
                    transaction,
                    characterId);
                transaction.Commit();
                return snapshot;
            }
        }

        internal List<DungeonPermissionEntrySnapshot> RecordClearAndLoad(
            int characterId,
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates,
            DateTime utcNow,
            out List<DungeonPermissionEntrySnapshot> changes)
        {
            ValidateCharacterId(characterId);
            var normalized = NormalizeUpdates(updates);
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction(deferred: false))
            {
                EnsureCurrentDay(
                    connection,
                    transaction,
                    characterId,
                    utcNow);
                changes = new List<DungeonPermissionEntrySnapshot>();
                foreach (var update in normalized)
                {
                    if (!Upsert(
                            connection,
                            transaction,
                            characterId,
                            update.DungeonId,
                            update.ClearState))
                    {
                        continue;
                    }

                    changes.Add(new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = update.DungeonId,
                        ClearState = update.ClearState,
                    });
                }

                var snapshot = Load(
                    connection,
                    transaction,
                    characterId);
                transaction.Commit();
                return snapshot;
            }
        }

        private void EnsureCurrentDay(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DateTime utcNow)
        {
            var marker = _dailyReset.GetCounter(
                connection,
                transaction,
                characterId,
                MarkerKey,
                DailyResetService.PeriodDay,
                utcNow);
            if (marker == 1)
                return;
            if (marker != 0)
            {
                throw new InvalidOperationException(
                    $"Invalid Anton Awakening daily progress marker value: {marker}.");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
DELETE FROM character_dungeon_permissions
WHERE character_id = @cid
  AND dungeon_id IN (243, 244, 245, 246, 247);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }

            if (!_dailyReset.TryClaimFlag(
                    connection,
                    transaction,
                    characterId,
                    MarkerKey,
                    DailyResetService.PeriodDay,
                    utcNow)
                || _dailyReset.GetCounter(
                    connection,
                    transaction,
                    characterId,
                    MarkerKey,
                    DailyResetService.PeriodDay,
                    utcNow) != 1)
            {
                throw new InvalidOperationException(
                    "Unable to claim the Anton Awakening daily progress marker.");
            }
        }

        private static List<DungeonPermissionEntrySnapshot> NormalizeUpdates(
            IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates)
        {
            if (updates == null)
                throw new ArgumentNullException(nameof(updates));

            var result = new List<DungeonPermissionEntrySnapshot>();
            var indexes = new Dictionary<ushort, int>();
            foreach (var update in updates)
            {
                if (update == null
                    || update.DungeonId < 243
                    || update.DungeonId > 247
                    || update.ClearState == 0)
                {
                    throw new ArgumentException(
                        "Anton Awakening progress updates require dungeon IDs 243-247 and non-zero states.",
                        nameof(updates));
                }

                if (indexes.TryGetValue(update.DungeonId, out var index))
                {
                    if (result[index].ClearState < update.ClearState)
                        result[index].ClearState = update.ClearState;
                    continue;
                }

                indexes.Add(update.DungeonId, result.Count);
                result.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = update.DungeonId,
                    ClearState = update.ClearState,
                });
            }
            return result;
        }

        private static bool Upsert(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int dungeonId,
            byte clearState)
        {
            var existingRows = 0;
            var currentState = 0;
            using (var command = new SqliteCommand(@"
SELECT COUNT(*), COALESCE(MAX(clear_state), 0)
FROM character_dungeon_permissions
WHERE character_id = @cid AND dungeon_id = @did;",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@did", dungeonId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        existingRows = reader.GetInt32(0);
                        currentState = reader.GetInt32(1);
                    }
                }
            }

            if (currentState >= clearState)
                return false;

            if (existingRows > 0)
            {
                using (var command = new SqliteCommand(@"
UPDATE character_dungeon_permissions
SET clear_state = @state
WHERE character_id = @cid AND dungeon_id = @did;",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@state", clearState);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.ExecuteNonQuery();
                }
            }
            else
            {
                using (var command = new SqliteCommand(@"
INSERT INTO character_dungeon_permissions
    (character_id, sort_order, dungeon_id, clear_state)
VALUES
    (@cid,
     (SELECT COALESCE(MAX(sort_order), 0) + 1
      FROM character_dungeon_permissions
      WHERE character_id = @cid),
     @did,
     @state);",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.Parameters.AddWithValue("@state", clearState);
                    command.ExecuteNonQuery();
                }
            }
            return true;
        }

        private static List<DungeonPermissionEntrySnapshot> Load(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            using (var command = new SqliteCommand(@"
SELECT dungeon_id, clear_state
FROM character_dungeon_permissions
WHERE character_id = @cid
  AND dungeon_id IN (243, 244, 245, 246, 247)
ORDER BY sort_order;",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = (ushort)reader.GetInt32(0),
                            ClearState = (byte)reader.GetInt32(1),
                        });
                    }
                }
            }
            return result;
        }

        private static void ValidateCharacterId(int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
        }
    }
}
