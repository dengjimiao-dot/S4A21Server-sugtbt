using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Globalization;
using DfoServer.Game.Administration;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Admin;

internal sealed record AuctionAdminResult(bool Success, string Code, string Message, object Data);
internal sealed class AuctionAdminConflictException : InvalidOperationException { }
internal enum AuctionAdminFaultPoint { AfterPublicReceiptBeforePrivateComplete }
internal sealed record AuctionAdminGrant(InventoryListType List, short Slot, string EnvelopeJson, int Quantity);

// These are part of the frozen intent, never an out-of-band callback. Ordinary
// rail intents remain byte-for-byte v1. Only Action.Admin can carry this block.
internal sealed record AuctionAdminEffects(int GoldCredit, IReadOnlyList<AuctionAdminGrant> Grants, int? GoldBefore=null)
{
    internal static readonly AuctionAdminEffects None = new(0, Array.Empty<AuctionAdminGrant>());
    internal string Serialize() => JsonSerializer.Serialize(this);
    internal static AuctionAdminEffects Read(AuctionOperationIntent intent, AuctionItemCodec codec)
    {
        if (intent.Action != AuctionOperationAction.Admin) return None;
        using var doc = JsonDocument.Parse(intent.IntentJson);
        var text = doc.RootElement.GetProperty("adminEffects").GetString();
        var value = JsonSerializer.Deserialize<AuctionAdminEffects>(text);
        if (value == null || value.Serialize() != text || value.GoldCredit < 0 || value.GoldBefore<0 || value.Grants == null
            || value.Grants.Count > 10 || value.Grants.Any(g=>g==null) || value.Grants.Select(g => (g.List, g.Slot)).Distinct().Count() != value.Grants.Count)
            throw new ArgumentException("Invalid frozen administrative inventory effects.");
        foreach (var grant in value.Grants)
        {
            var core = codec.DecodeStrict(codec.DeserializeStrict(grant.EnvelopeJson));
            if (grant.List!=InventoryListType.Main || grant.Slot < 0 || grant.Quantity <= 0
                || core.ItemKind is ItemCore.KindAvatar or ItemCore.KindCreature
                || (InventoryStackRuleService.IsStackable(core) ? core.Count != grant.Quantity : grant.Quantity != 1))
                throw new ArgumentException("Direct grants require an ordinary full instance and an explicit empty Main slot. Detail instances use strict mail delivery.");
        }
        return value;
    }
}

internal sealed record AuctionAdminDescriptor(string RequestId, string PayloadHash, string Command,
    string Operator, string Reason, long GoldAdjustment, IReadOnlyDictionary<string, long> ItemAdjustments,
    string UndoRequestId, string QaTag)
{
    internal string Serialize() => JsonSerializer.Serialize(this);
    internal static AuctionAdminDescriptor Read(AuctionOperationIntent intent)
    {
        using var doc = JsonDocument.Parse(intent.IntentJson);
        var text = doc.RootElement.GetProperty("adminChange").GetString();
        var value = JsonSerializer.Deserialize<AuctionAdminDescriptor>(text);
        if (value == null || value.Serialize() != text || string.IsNullOrWhiteSpace(value.RequestId)
            || value.PayloadHash?.Length != 64 || string.IsNullOrWhiteSpace(value.Operator)
            || string.IsNullOrWhiteSpace(value.Reason) || value.ItemAdjustments == null)
            throw new ArgumentException("Invalid administrative provenance.");
        return value;
    }
}

internal static class AuctionAdminJson
{
    internal static string RequestHash(GmAdminRequest request)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();writer.WriteString("requestId",request.RequestId);writer.WriteString("operation","auction");
            if(request.TargetAccountId.HasValue)writer.WriteNumber("targetAccountId",request.TargetAccountId.Value);else writer.WriteNull("targetAccountId");
            if(request.TargetCharacterId.HasValue)writer.WriteNumber("targetCharacterId",request.TargetCharacterId.Value);else writer.WriteNull("targetCharacterId");
            writer.WriteBoolean("dryRun",request.DryRun);writer.WritePropertyName("args");Canonical(writer,request.Args);writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Canonical(Utf8JsonWriter writer,JsonElement value)
    {
        switch(value.ValueKind) {
            case JsonValueKind.Object:
                writer.WriteStartObject();var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var p in value.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)) {
                    if(!seen.Add(p.Name))throw new ArgumentException("Duplicate JSON field: "+p.Name);
                    writer.WritePropertyName(p.Name);Canonical(writer,p.Value);
                }writer.WriteEndObject();break;
            case JsonValueKind.Array:writer.WriteStartArray();foreach(var item in value.EnumerateArray())Canonical(writer,item);writer.WriteEndArray();break;
            case JsonValueKind.String:writer.WriteStringValue(value.GetString());break;
            case JsonValueKind.Number:
                if(!value.TryGetDecimal(out var number))throw new ArgumentException("Administrative number exceeds decimal range.");
                writer.WriteRawValue(number.ToString("G29",CultureInfo.InvariantCulture));break;
            default:value.WriteTo(writer);break;
        }
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string SnapshotHash(AuctionOrderSnapshot value) => Hash(JsonSerializer.Serialize(value));
    internal static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected JSON object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            if (!allowed.Contains(p.Name, StringComparer.Ordinal) || !seen.Add(p.Name))
                throw new ArgumentException("Unknown or duplicate field: " + p.Name);
    }
    internal static string Text(JsonElement value, string key, string fallback = null)
        => value.TryGetProperty(key, out var p) ? p.GetString() : fallback;
    internal static long Number(JsonElement value, string key, long fallback = 0)
        => value.TryGetProperty(key, out var p) ? Integer(p) : fallback;
    internal static long Integer(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Number||!value.TryGetDecimal(out var n)||decimal.Truncate(n)!=n||n<long.MinValue||n>long.MaxValue)
            throw new ArgumentException("Expected an exact signed integer.");
        return (long)n;
    }
    internal static string Normalize(JsonElement value)
    {using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream))Canonical(writer,value);return Encoding.UTF8.GetString(stream.ToArray());}
    internal static bool Flag(JsonElement value, string key)
        => value.TryGetProperty(key, out var p) && p.GetBoolean();
}
