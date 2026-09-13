using System;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    internal enum AntonAwakeningRewardGrantOutcome
    {
        Failed = 0,
        Granted = 1,
        AlreadyClaimed = 2,
    }

    internal sealed class AntonAwakeningRewardGrantResult
    {
        internal AntonAwakeningRewardGrantResult(
            AntonAwakeningRewardGrantOutcome outcome,
            AntonAwakeningRewardDefinition reward,
            InventoryMutationSet changes = null)
        {
            Outcome = outcome;
            Reward = reward;
            Changes = changes ?? new InventoryMutationSet();
        }

        internal AntonAwakeningRewardGrantOutcome Outcome { get; }
        internal AntonAwakeningRewardDefinition Reward { get; }
        internal InventoryMutationSet Changes { get; }
    }

    internal sealed class AntonAwakeningRewardGrantService
    {
        private readonly AntonAwakeningDailyCardService _dailyRewards;

        internal AntonAwakeningRewardGrantService(
            AntonAwakeningDailyCardService dailyRewards)
        {
            _dailyRewards = dailyRewards
                ?? throw new ArgumentNullException(nameof(dailyRewards));
        }

        internal AntonAwakeningRewardGrantResult TryGrant(
            InventoryLease lease,
            AntonAwakeningRewardDefinition reward)
        {
            if (lease == null || !reward.IsValid)
            {
                return new AntonAwakeningRewardGrantResult(
                    AntonAwakeningRewardGrantOutcome.Failed,
                    reward);
            }

            var alreadyClaimed = false;
            InventoryRewardGrantResult inventoryResult = null;
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "anton-awakening-auto-reward",
                (connection, transaction) =>
                {
                    if (!_dailyRewards.TryClaimReward(
                            connection,
                            transaction,
                            lease.CharacterId))
                    {
                        alreadyClaimed = true;
                        return true;
                    }

                    return InventoryRewardGrantService.TryCreateAndInsert(
                        lease,
                        reward.ItemId,
                        ItemCreateReason.DungeonDrop,
                        1,
                        out inventoryResult);
                });
            if (!committed)
            {
                return new AntonAwakeningRewardGrantResult(
                    AntonAwakeningRewardGrantOutcome.Failed,
                    reward);
            }
            if (alreadyClaimed)
            {
                return new AntonAwakeningRewardGrantResult(
                    AntonAwakeningRewardGrantOutcome.AlreadyClaimed,
                    reward);
            }

            return new AntonAwakeningRewardGrantResult(
                AntonAwakeningRewardGrantOutcome.Granted,
                reward,
                inventoryResult?.Changes);
        }
    }
}
