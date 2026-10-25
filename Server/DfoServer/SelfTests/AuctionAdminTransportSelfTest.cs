using System;
using System.Collections.Generic;
using System.Text.Json;
using DfoServer.Game.Administration;

namespace DfoServer.SelfTests
{
    // No socket, pipe, HTTP, database or FileLogger is opened by this test.
    public static class AuctionAdminTransportSelfTest
    {
        public static int Run()
        {
            var failures = 0;
            void Check(string name, bool condition)
            {
                Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + name);
                if (!condition) failures++;
            }
            const string request = "{\"requestId\":\"api-1\",\"operation\":\"auction\",\"targetAccountId\":1,\"targetCharacterId\":4,\"dryRun\":false,\"args\":{\"command\":\"bid\",\"operator\":\"ops\",\"reason\":\"reviewed\",\"data\":{\"orderId\":5,\"expectedVersion\":4,\"amount\":1000}}}";
            Check("auction shell accepted", GmAdminRequest.TryParse(request, out var parsed, out _)
                && parsed.Operation == GmAdminOperation.Auction && parsed.TargetCharacterId == 4 && !parsed.DryRun);
            Check("same request same payload hash", GmAdminRequest.TryParse(request, out var same, out _)
                && same.PayloadHash == parsed.PayloadHash);
            Check("same ID changed amount changes hash", GmAdminRequest.TryParse(request.Replace("1000", "1001"), out var changed, out _)
                && changed.PayloadHash != parsed.PayloadHash);
            Check("unknown top field refused", !GmAdminRequest.TryParse(request.Replace("\"operation\":", "\"rawSql\":\"x\",\"operation\":"), out _, out _));
            Check("duplicate operation refused", !GmAdminRequest.TryParse(request.Replace("\"operation\":", "\"operation\":\"health\",\"operation\":"), out _, out _));
            Check("nonboolean dryRun refused", !GmAdminRequest.TryParse(request.Replace("\"dryRun\":false", "\"dryRun\":0"), out _, out _));
            Check("negative target refused", !GmAdminRequest.TryParse(request.Replace("\"targetCharacterId\":4", "\"targetCharacterId\":-1"), out _, out _));
            Check("oversized shell refused", !GmAdminRequest.TryParse(new string('x', GmAdminRequest.MaximumRequestBytes + 1), out _, out _));
            Check("unrelated management operation refused", GmAdminOperations.Parse("character.offlineGm") == GmAdminOperation.Unknown);

            var response = GmAdminResponse.Ok("auction", "api-1");
            var mutable = new Dictionary<string, object> { ["itemId"] = 27602, ["integer"] = 9007199254740993L, ["core"] = new byte[] { 0, 255 } };
            response.SetAuctionData(mutable);
            var frozenJson = response.ToJson();
            mutable["itemId"] = 0;
            Check("response freezes mutable data", frozenJson == response.ToJson());
            using var document = JsonDocument.Parse(frozenJson);
            var data = document.RootElement.GetProperty("data");
            Check("payload is data object", data.GetProperty("itemId").GetInt32() == 27602);
            Check("int64 precision preserved", data.GetProperty("integer").GetInt64() == 9007199254740993L);
            Check("binary payload retained", data.GetProperty("core").GetBytesFromBase64().AsSpan().SequenceEqual(new byte[] { 0, 255 }));
            using var legacy = JsonDocument.Parse(GmAdminResponse.Ok("health", "legacy-1").ToJson());
            Check("legacy envelope does not acquire data", !legacy.RootElement.TryGetProperty("data", out _));
            Console.WriteLine($"Auction admin transport: failures={failures}");
            return failures == 0 ? 0 : 1;
        }
    }
}
