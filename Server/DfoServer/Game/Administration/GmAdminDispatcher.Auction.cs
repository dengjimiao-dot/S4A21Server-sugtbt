using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Auction.Admin;

namespace DfoServer.Game.Administration
{
    internal sealed partial class GmAdminDispatcher
    {
        private readonly AuctionAdminService _auctionAdmin;

        internal GmAdminDispatcher(AuctionAdminService auctionAdmin)
            => _auctionAdmin = auctionAdmin ?? throw new ArgumentNullException(nameof(auctionAdmin));

        internal Task<GmAdminResponse> DispatchAsync(GmAdminRequest request, CancellationToken cancellationToken)
            => request?.Operation == GmAdminOperation.Auction
                ? HandleAuctionAsync(request, cancellationToken)
                : Task.FromResult(GmAdminResponse.Fail("unsupported-operation", "Use operation=auction."));

        private async Task<GmAdminResponse> HandleAuctionAsync(
            GmAdminRequest request, CancellationToken cancellationToken)
        {
            if (_auctionAdmin == null)
                return GmAdminResponse.Fail("auction-unavailable", "Auction administration is not wired.",
                    request.OperationText, request.RequestId);

            // The auction service is the only authorization/parser/transaction
            // owner. In particular this does not broaden the QA-only paths.
            try
            {
                var result = await _auctionAdmin.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
                var response = result.Success
                    ? GmAdminResponse.Ok(request.OperationText, request.RequestId)
                    : GmAdminResponse.Fail(result.Code, result.Message, request.OperationText, request.RequestId);
                response.Code = result.Code;
                response.Detail = result.Message;
                response.DryRun = request.DryRun;
                response.SetAuctionData(result.Data);

                if (Encoding.UTF8.GetByteCount(response.ToJson()) > GmAdminRequest.MaximumRequestBytes)
                {
                    // Never truncate a page or quietly omit instance/receipt data.
                    // A write may already have committed; this is not a rollback.
                    return GmAdminResponse.Fail("response-too-large",
                        "Result exceeds 64 KiB. Query this requestId and use a smaller page; do not repeat the write with a new ID.",
                        request.OperationText, request.RequestId);
                }
                return response;
            }
            catch (Exception)
            {
                // The owner may have crossed a durable boundary before failing.
                // Keep the pipe alive, expose no exception content, never retry.
                return GmAdminResponse.Fail("unknown-outcome",
                    "Auction result unavailable. Query this exact requestId before any retry.",
                    request.OperationText, request.RequestId);
            }
        }
    }
}
