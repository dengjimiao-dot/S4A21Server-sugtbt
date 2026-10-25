// The composition root must supply AccountTutorialDatabase's initialized
// IGameDatabase. The caller binds accountId to the authenticated current session,
// admits only the proven mode1 namespace, and checks the original uint flag <=15
// before any byte cast. Mark(false) means already present, not a failed write.
using System;
using System.Collections.Generic;
using System.IO;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Tutorials
{
    internal sealed class AccountTutorialFlagStore
    {
        internal const int FlagCount = 16;
        private readonly IGameDatabase database;

        internal AccountTutorialFlagStore(IGameDatabase initializedPrivateDatabase)
        {
            database = initializedPrivateDatabase
                ?? throw new ArgumentNullException(nameof(initializedPrivateDatabase));
        }

        internal IReadOnlyList<byte> Load(int accountId)
        {
            ValidateAccount(accountId);
            return database.Read<IReadOnlyList<byte>>(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT flag_index, typeof(flag_index)
FROM account_tutorial_flags
WHERE account_id = @account
ORDER BY flag_index;";
                command.Parameters.AddWithValue("@account", accountId);
                using var reader = command.ExecuteReader();
                var flags = new List<byte>(FlagCount);
                long previous = -1;
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.GetString(1) != "integer")
                        throw new InvalidDataException("Invalid persisted account tutorial flag type.");
                    long value = reader.GetInt64(0);
                    if (value < 0 || value >= FlagCount || value <= previous
                        || flags.Count >= FlagCount)
                        throw new InvalidDataException("Invalid persisted account tutorial flag set.");
                    flags.Add((byte)value);
                    previous = value;
                }
                return flags.AsReadOnly();
            });
        }

        internal bool Mark(int accountId, byte flag)
        {
            ValidateAccount(accountId);
            if (flag >= FlagCount)
                throw new ArgumentOutOfRangeException(nameof(flag));
            return database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO account_tutorial_flags (account_id, flag_index)
VALUES (@account, @flag)
ON CONFLICT(account_id, flag_index) DO NOTHING;";
                command.Parameters.AddWithValue("@account", accountId);
                command.Parameters.AddWithValue("@flag", (int)flag);
                int changed = command.ExecuteNonQuery();
                if (changed != 0 && changed != 1)
                    throw new InvalidDataException("Unexpected account tutorial insert count.");
                return changed == 1;
            });
        }

        private static void ValidateAccount(int accountId)
        {
            if (accountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(accountId));
        }
    }
}
