using System;
using System.IO;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Tutorials;

internal static class AccountTutorialDatabase
{
    internal static IGameDatabase Open()
        => Open(Environment.GetEnvironmentVariable("ACCOUNT_TUTORIAL_DATABASE_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "Data", "account-tutorial.db"));

    internal static IGameDatabase Open(string path)
        => FeatureDatabaseBootstrap.Open(path, "a21-account-tutorial-v1", @"
CREATE TABLE account_tutorial_flags (
    account_id INTEGER NOT NULL CHECK(typeof(account_id) = 'integer' AND account_id > 0),
    flag_index INTEGER NOT NULL CHECK(typeof(flag_index) = 'integer' AND flag_index BETWEEN 0 AND 15),
    PRIMARY KEY(account_id, flag_index)
);");
}
