using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DfoServer.Game.Administration
{
    // 管理 IPC 的唯一操作集合。这里是闭集：任何其它字符串都返回 unsupported，
    // 不存在 rawSQL / rawpath / 反射 / 任意 method 名透传通道。
    internal enum GmAdminOperation { Unknown = 0, Auction = 7 }
    internal static class GmAdminOperations
    {
        internal const string Auction = "auction";
        internal static readonly string[] Supported = { Auction };
        internal static GmAdminOperation Parse(string operation)
            => operation == Auction ? GmAdminOperation.Auction : GmAdminOperation.Unknown;
    }

    /// <summary>
    /// 一行 UTF8 JSON 请求。字段闭集：
    ///   requestId(string, 1..128, [A-Za-z0-9._-], 不含 "..")
    ///   operation(string, 见 GmAdminOperations)
    ///   targetAccountId(int? &gt; 0)
    ///   targetCharacterId(int? &gt; 0)
    ///   args(object?, 操作专属, 由各操作白名单解析)
    ///   dryRun(bool?, auction 使用)
    /// 出现任何未知顶层字段即拒绝，防止请求自带 manifest / direct / path 之类夹带。
    /// </summary>
    internal sealed class GmAdminRequest
    {
        internal const int MaximumRequestBytes = 64 * 1024;
        internal const int MaximumRequestIdLength = 128;

        private static readonly HashSet<string> AllowedFields =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "requestId",
                "operation",
                "targetAccountId",
                "targetCharacterId",
                "args",
                "dryRun",
            };

        private GmAdminRequest()
        {
        }

        internal string RequestId { get; private set; }

        internal string OperationText { get; private set; }

        internal GmAdminOperation Operation { get; private set; }

        internal int? TargetAccountId { get; private set; }

        internal int? TargetCharacterId { get; private set; }

        internal JsonElement Args { get; private set; }

        internal bool DryRun { get; private set; }

        /// <summary>
        /// requestId + 规范化 payload 的 SHA-256。用于文件 receipt 的互斥和重放判定：
        /// 同 id 同 hash 视为重放，同 id 不同 hash 视为冲突。
        /// </summary>
        internal string PayloadHash { get; private set; }

        internal static bool IsValidRequestId(string requestId)
        {
            if (string.IsNullOrEmpty(requestId)
                || requestId.Length > MaximumRequestIdLength)
            {
                return false;
            }

            if (requestId.IndexOf("..", StringComparison.Ordinal) >= 0)
                return false;

            foreach (var ch in requestId)
            {
                var isDigit = ch >= '0' && ch <= '9';
                var isUpper = ch >= 'A' && ch <= 'Z';
                var isLower = ch >= 'a' && ch <= 'z';
                if (!isDigit && !isUpper && !isLower && ch != '.' && ch != '_' && ch != '-')
                    return false;
            }

            return true;
        }

        internal static bool TryParse(
            string line,
            out GmAdminRequest request,
            out string error)
        {
            request = null;
            error = null;

            if (line == null)
            {
                error = "request-empty";
                return false;
            }

            var byteCount = Encoding.UTF8.GetByteCount(line);
            if (byteCount > MaximumRequestBytes)
            {
                error = "request-too-large";
                return false;
            }

            if (line.Length == 0)
            {
                error = "request-empty";
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(
                    line,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8,
                    });
            }
            catch (JsonException)
            {
                error = "request-not-json";
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    error = "request-not-object";
                    return false;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                {
                    if (!AllowedFields.Contains(property.Name))
                    {
                        error = "request-unknown-field:" + property.Name;
                        return false;
                    }

                    if (!seen.Add(property.Name))
                    {
                        error = "request-duplicate-field:" + property.Name;
                        return false;
                    }
                }

                if (!root.TryGetProperty("requestId", out var requestIdElement)
                    || requestIdElement.ValueKind != JsonValueKind.String)
                {
                    error = "request-missing-requestId";
                    return false;
                }

                var requestId = requestIdElement.GetString();
                if (!IsValidRequestId(requestId))
                {
                    error = "request-invalid-requestId";
                    return false;
                }

                if (!root.TryGetProperty("operation", out var operationElement)
                    || operationElement.ValueKind != JsonValueKind.String)
                {
                    error = "request-missing-operation";
                    return false;
                }

                var operationText = operationElement.GetString();
                if (string.IsNullOrEmpty(operationText))
                {
                    error = "request-missing-operation";
                    return false;
                }

                int? accountId = null;
                if (root.TryGetProperty("targetAccountId", out var accountElement))
                {
                    if (accountElement.ValueKind != JsonValueKind.Number
                        || !accountElement.TryGetInt32(out var parsedAccount)
                        || parsedAccount <= 0)
                    {
                        error = "request-invalid-targetAccountId";
                        return false;
                    }

                    accountId = parsedAccount;
                }

                int? characterId = null;
                if (root.TryGetProperty("targetCharacterId", out var characterElement))
                {
                    if (characterElement.ValueKind != JsonValueKind.Number
                        || !characterElement.TryGetInt32(out var parsedCharacter)
                        || parsedCharacter <= 0)
                    {
                        error = "request-invalid-targetCharacterId";
                        return false;
                    }

                    characterId = parsedCharacter;
                }

                var args = default(JsonElement);
                var argsRaw = string.Empty;
                if (root.TryGetProperty("args", out var argsElement))
                {
                    if (argsElement.ValueKind != JsonValueKind.Object)
                    {
                        error = "request-invalid-args";
                        return false;
                    }

                    args = argsElement.Clone();
                    argsRaw = args.GetRawText();
                }

                var dryRun = false;
                if (root.TryGetProperty("dryRun", out var dryRunElement))
                {
                    if (dryRunElement.ValueKind != JsonValueKind.True
                        && dryRunElement.ValueKind != JsonValueKind.False)
                    {
                        error = "request-invalid-dryRun";
                        return false;
                    }

                    dryRun = dryRunElement.GetBoolean();
                }

                request = new GmAdminRequest
                {
                    RequestId = requestId,
                    OperationText = operationText,
                    Operation = GmAdminOperations.Parse(operationText),
                    TargetAccountId = accountId,
                    TargetCharacterId = characterId,
                    Args = args,
                    DryRun = dryRun,
                    PayloadHash = ComputePayloadHash(
                        requestId,
                        operationText,
                        accountId,
                        characterId,
                        dryRun,
                        argsRaw),
                };
                return true;
            }
        }

        private static string ComputePayloadHash(
            string requestId,
            string operationText,
            int? accountId,
            int? characterId,
            bool dryRun,
            string argsRaw)
        {
            var canonical = new StringBuilder(256);
            canonical.Append("rid=").Append(requestId).Append('\n');
            canonical.Append("op=").Append(operationText).Append('\n');
            canonical.Append("acc=")
                .Append(accountId.HasValue
                    ? accountId.Value.ToString(CultureInfo.InvariantCulture)
                    : "-")
                .Append('\n');
            canonical.Append("chr=")
                .Append(characterId.HasValue
                    ? characterId.Value.ToString(CultureInfo.InvariantCulture)
                    : "-")
                .Append('\n');
            canonical.Append("dry=").Append(dryRun ? "1" : "0").Append('\n');
            canonical.Append("args=").Append(argsRaw ?? string.Empty).Append('\n');

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(canonical.ToString()));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var value in hash)
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }
    }

    /// <summary>
    /// 一行 UTF8 JSON 响应。只输出显式字段，避免把异常、请求原文、
    /// HTTP header 或凭据带出进程。
    /// </summary>
    internal sealed class GmAdminResponse
    {
        internal bool Success { get; set; }

        /// <summary>机器可读结果码；成功固定为 "ok"。</summary>
        internal string Code { get; set; }

        internal string Detail { get; set; }

        internal string RequestId { get; set; }

        internal string Operation { get; set; }

        internal bool? Online { get; set; }

        internal string SessionId { get; set; }

        internal bool? CurrentLease { get; set; }

        internal string LeaseSessionId { get; set; }

        internal long? LeaseVersion { get; set; }

        internal bool? RequiresOffline { get; set; }

        internal bool? DryRun { get; set; }

        internal string Action { get; set; }

        internal string HttpPath { get; set; }

        internal int? HttpStatus { get; set; }

        internal string ReceiptState { get; set; }

        internal bool? Completed { get; set; }

        internal string RegistryStatus { get; set; }

        internal string PipeName { get; set; }

        // Only the auction owner supplies this typed payload. Legacy operations
        // retain their existing response fields and never acquire a data field.
        internal JsonElement? Data { get; private set; }

        private static readonly JsonSerializerOptions AuctionJsonOptions = new(JsonSerializerDefaults.Web);

        internal void SetAuctionData(object value)
        {
            // Freeze the payload once so the bounded preflight and actual pipe
            // write serialize the same data, even if an owner used mutable DTOs.
            Data = value == null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), AuctionJsonOptions);
        }

        internal static GmAdminResponse Ok(string operation, string requestId)
        {
            return new GmAdminResponse
            {
                Success = true,
                Code = "ok",
                Operation = operation,
                RequestId = requestId,
            };
        }

        internal static GmAdminResponse Fail(
            string code,
            string detail,
            string operation = null,
            string requestId = null)
        {
            return new GmAdminResponse
            {
                Success = false,
                Code = code,
                Detail = detail,
                Operation = operation,
                RequestId = requestId,
            };
        }

        internal string ToJson()
        {
            using (var stream = new MemoryStream(512))
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("success", Success);
                    WriteString(writer, "code", Code);
                    WriteString(writer, "detail", Detail);
                    WriteString(writer, "requestId", RequestId);
                    WriteString(writer, "operation", Operation);
                    WriteBoolean(writer, "online", Online);
                    WriteString(writer, "sessionId", SessionId);
                    WriteBoolean(writer, "currentLease", CurrentLease);
                    WriteString(writer, "leaseSessionId", LeaseSessionId);
                    if (LeaseVersion.HasValue)
                        writer.WriteNumber("leaseVersion", LeaseVersion.Value);
                    WriteBoolean(writer, "requiresOffline", RequiresOffline);
                    WriteBoolean(writer, "dryRun", DryRun);
                    WriteString(writer, "action", Action);
                    WriteString(writer, "httpPath", HttpPath);
                    if (HttpStatus.HasValue)
                        writer.WriteNumber("httpStatus", HttpStatus.Value);
                    WriteString(writer, "receiptState", ReceiptState);
                    WriteBoolean(writer, "completed", Completed);
                    WriteString(writer, "registryStatus", RegistryStatus);
                    WriteString(writer, "pipeName", PipeName);
                    if (Data.HasValue)
                    {
                        writer.WritePropertyName("data");
                        Data.Value.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void WriteString(
            Utf8JsonWriter writer,
            string name,
            string value)
        {
            if (value == null)
                return;

            writer.WriteString(name, value);
        }

        private static void WriteBoolean(
            Utf8JsonWriter writer,
            string name,
            bool? value)
        {
            if (!value.HasValue)
                return;

            writer.WriteBoolean(name, value.Value);
        }
    }
}
