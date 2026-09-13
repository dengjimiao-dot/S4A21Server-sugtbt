using System;
using DfoServer.Game.DailyReset;

namespace DfoServer.Game.Dungeon
{
    /// <summary>
    /// Daily per-dungeon monster-loot gate for Anton Awakening dungeons 243-247.
    /// The shared DailyResetService remains the persistent owner and rolls over
    /// each counter at the Beijing 06:00 game-day boundary.
    /// </summary>
    internal sealed class AntonAwakeningDailyLootGuard
    {
        private const string LootCounterKeyPrefix = "anton_awakening_loot_";
        private readonly DailyResetService _dailyReset;

        internal AntonAwakeningDailyLootGuard(DailyResetService dailyReset)
        {
            _dailyReset = dailyReset
                ?? throw new ArgumentNullException(nameof(dailyReset));
        }

        internal bool TryMarkLootClaimed(int characterId, int dungeonId)
        {
            return characterId > 0
                && IsAntonAwakeningDungeon(dungeonId)
                && _dailyReset.TryIncrementCounter(
                    characterId,
                    LootCounterKeyPrefix + dungeonId,
                    cap: 1,
                    period: DailyResetService.PeriodDay);
        }

        internal bool HasClaimedLootToday(int characterId, int dungeonId)
        {
            return characterId > 0
                && IsAntonAwakeningDungeon(dungeonId)
                && _dailyReset.GetCounter(
                    characterId,
                    LootCounterKeyPrefix + dungeonId) > 0;
        }

        internal static bool IsAntonAwakeningDungeon(int dungeonId)
            => dungeonId >= 243 && dungeonId <= 247;
    }
}
