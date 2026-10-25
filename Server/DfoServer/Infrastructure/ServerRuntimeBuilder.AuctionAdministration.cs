using System;
using System.Threading;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Admin;
using DfoServer.Game.Auction.Storage;

namespace DfoServer.Infrastructure;

public sealed partial class ServerRuntimeBuilder
{
    private GmAdminPipeHost _administration;
    private GmAdminCharacterWriteFenceStore _adminCharacterWriteFences;

    internal GmAdminCharacterWriteFenceStore GetOrCreateAdminCharacterWriteFences()
    {
        var current = Volatile.Read(ref _adminCharacterWriteFences);
        if (current != null) return current;
        var created = new GmAdminCharacterWriteFenceStore(GmAdminCharacterWriteFenceStore.DefaultDirectory);
        return Interlocked.CompareExchange(ref _adminCharacterWriteFences, created, null) ?? created;
    }

    private void StartAdministration(GameProtocolCoreDependencies core, GameProtocolWorldDependencies world)
    {
        if (!GmAdminPipeHost.IsEnabledByEnvironment() || _auctionRuntime == null) return;
        var host = new GmAdminPipeHost(new GmAdminDispatcher(new AuctionAdminService(
            _auctionRuntime, Database, AuctionDatabase.DefaultPath, core.CharacterRepository,
            world.Sessions, world.CharacterTransitions, GetOrCreateAdminCharacterWriteFences())));
        try
        {
            if (host.TryStart()) _administration = host;
            else host.Dispose();
        }
        catch (Exception ex)
        {
            host.Dispose();
            FileLogger.Log("[AuctionAdmin] startup unavailable: " + ex.GetType().Name);
        }
    }

    private void DisposeAdministration() => _administration?.Dispose();
}
