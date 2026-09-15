using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class PartyPacketSendResult
    {
        internal PartyPacketSendResult(
            IReadOnlyList<DungeonParticipantRosterEntry> succeeded,
            IReadOnlyList<DungeonParticipantRosterEntry> failed)
        {
            Succeeded = succeeded
                ?? Array.Empty<DungeonParticipantRosterEntry>();
            Failed = failed
                ?? Array.Empty<DungeonParticipantRosterEntry>();
        }

        internal IReadOnlyList<DungeonParticipantRosterEntry> Succeeded
        {
            get;
        }

        internal IReadOnlyList<DungeonParticipantRosterEntry> Failed
        {
            get;
        }
    }

    internal sealed class PartyPacketSender
    {
        private readonly ISessionDirectory _sessions;

        internal PartyPacketSender(ISessionDirectory sessions)
        {
            _sessions = sessions;
        }

        internal async Task<PartyPacketSendResult> SendToPartyAsync(
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            IReadOnlyList<byte[]> packets)
        {
            var orderedRoster = OrderRoster(roster);
            var succeeded = new List<DungeonParticipantRosterEntry>(
                orderedRoster.Count);
            var failed = new List<DungeonParticipantRosterEntry>();

            if (packets == null || packets.Any(packet => packet == null))
            {
                failed.AddRange(orderedRoster);
                return new PartyPacketSendResult(
                    succeeded.AsReadOnly(),
                    failed.AsReadOnly());
            }

            foreach (var participant in orderedRoster)
            {
                if (!TryResolveCurrentSession(participant, out var session))
                {
                    failed.Add(participant);
                    continue;
                }

                var sent = true;
                try
                {
                    foreach (var packet in packets)
                    {
                        if (!await session.TrySendPacketAsync(
                                packet,
                                CancellationToken.None,
                                () => IsCurrentSession(
                                    participant,
                                    session)))
                        {
                            sent = false;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    sent = false;
                    FileLogger.Log(
                        $"[PartyPacketSender] packet batch failed: "
                        + $"cid={participant?.CharacterId ?? 0} "
                        + $"userId={participant?.ParticipantUserId ?? 0} "
                        + $"error={ex.GetType().Name}: {ex.Message}");
                }

                if (sent)
                    succeeded.Add(participant);
                else
                    failed.Add(participant);
            }

            return new PartyPacketSendResult(
                succeeded.AsReadOnly(),
                failed.AsReadOnly());
        }

        private bool TryResolveCurrentSession(
            DungeonParticipantRosterEntry participant,
            out EnhancedClientSession session)
        {
            session = null;
            return participant != null
                && _sessions != null
                && _sessions.TryGet(participant.CharacterId, out session)
                && IsCurrentSession(participant, session);
        }

        private bool IsCurrentSession(
            DungeonParticipantRosterEntry participant,
            EnhancedClientSession expectedSession)
        {
            if (participant == null
                || expectedSession == null
                || _sessions == null
                || !_sessions.TryGet(
                    participant.CharacterId,
                    out var currentSession)
                || !ReferenceEquals(currentSession, expectedSession))
            {
                return false;
            }

            var player = expectedSession.Player;
            return expectedSession.TcpClient != null
                && expectedSession.TcpClient.Connected
                && player != null
                && player.CharacterId == participant.CharacterId
                && player.UserId == participant.ParticipantUserId
                && ReferenceEquals(player.CurrentRun, participant.Run)
                && player.IsCurrentDungeonRun(participant.RunIdentity)
                && participant.Run.Matches(participant.RunIdentity);
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry>
            OrderRoster(IReadOnlyList<DungeonParticipantRosterEntry> roster)
        {
            if (roster == null || roster.Count == 0)
                return Array.Empty<DungeonParticipantRosterEntry>();

            return roster
                .OrderBy(value => value?.PartySlot ?? byte.MaxValue)
                .ThenBy(value =>
                    value?.ParticipantUserId ?? ushort.MaxValue)
                .ThenBy(value => value?.CharacterId ?? int.MaxValue)
                .ToList()
                .AsReadOnly();
        }
    }
}
