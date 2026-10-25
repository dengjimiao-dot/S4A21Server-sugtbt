using DfoServer.Game.SelectCharacter;
using DfoServer.Network;
using System;
using System.Linq;

namespace DfoServer.Network.Builders
{
    public sealed class DailyScheduleBodyBuilder : IInitPacketBuilder
    {
        // Legacy class name: A21 NOTI725 is account tutorial info, not a daily
        // schedule. 02F0DC30 -> 00ACEA60 clears all16 bits before each snapshot.
        public ushort NotiType => (ushort)NotiPacketTypeA21.ACCOUNT_TUTORIAL_INFO;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            if (occurrenceIndex != 0)
            {
                body = null;
                return false;
            }
            var flags = snapshot.InitializationSnapshot.AccountTutorialFlags;
            if (flags.Count > 16 || flags.Any(flag => flag >= 16) || flags.Distinct().Count() != flags.Count)
                throw new InvalidOperationException("Invalid account tutorial snapshot.");
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)flags.Count);
            foreach (var flag in flags.OrderBy(value => value))
                writer.WriteByte(flag);
            body = writer.ToArray();
            return true;
        }
    }
}
