using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DfoServer.Game.Administration
{
    /// <summary>
    /// 角色写围栏：每个 (accountId, characterId) 一份固定文件，持久化外部写意图。
    /// 调用方必须持有 CharacterTransitionCoordinator 角色门；内部 lock 只保证同一实例
    /// 的文件操作顺序，不是跨进程业务锁，无 TTL、无自动解锁、无 force-clear。纯同步
    /// 文件 IO，不建线程/后台循环，不访问数据库、HTTP 或玩家数据。
    /// </summary>
    internal sealed class GmAdminCharacterWriteFenceStore
    {
        private const string Pending = "pending", Unknown = "unknown", Resolved = "resolved";
        private const int MaximumRecordBytes = 16 * 1024;

        private static readonly HashSet<string> Fields = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema", "targetAccountId", "targetCharacterId",
            "requestId", "payloadHash", "state", "updatedAt",
        };

        private readonly object _sync = new object();
        private readonly string _directory;

        internal GmAdminCharacterWriteFenceStore(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("fence directory is required", nameof(directory));
            _directory = Path.GetFullPath(directory);
        }

        /// <summary>默认目录 ~/.dfoserver/auction/character-write-fences；只登记路径，不建目录。</summary>
        internal static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dfoserver", "auction", "character-write-fences");

        /// <summary>
        /// 只读。只有 FileNotFound/DirectoryNotFound 或合法 resolved 返回 false；损坏、
        /// 错误 schema、目标不符、未决/未知及任何 IO/权限异常 fail-closed 返回 true。
        /// </summary>
        internal bool IsBlocked(int accountId, int characterId, out string reason)
        {
            if (!TryBuildPath(accountId, characterId, out var path))
            {
                reason = "fence-invalid-target";
                return true;
            }
            lock (_sync)
            {
                var read = TryLoad(path, accountId, characterId, out var fence);
                if (read == LoadResult.Absent) { reason = null; return false; }
                if (read != LoadResult.Valid) { reason = FailureReason(read); return true; }
                if (fence.State == Resolved) { reason = null; return false; }
                reason = fence.State == Pending ? "fence-pending" : "fence-unknown";
                return true;
            }
        }

        /// <summary>
        /// 外部写调用前 durable 落盘 pending。旧记录未决/未知/损坏/不可读/目标不符时
        /// 拒绝覆盖；无记录或合法 resolved 才允许。true 表示已 Flush(true) 并原子替换。
        /// </summary>
        internal bool TryBegin(
            int accountId, int characterId, string requestId,
            string payloadHash, out string reason)
        {
            if (!Validate(accountId, characterId, requestId, payloadHash, out reason)
                || !TryBuildPath(accountId, characterId, out var path))
            {
                reason ??= "fence-path-invalid";
                return false;
            }
            lock (_sync)
            {
                var read = TryLoad(path, accountId, characterId, out var existing);
                if (read == LoadResult.Valid && existing.State != Resolved)
                {
                    reason = existing.State == Pending ? "fence-pending" : "fence-unknown";
                    return false;
                }
                if (read != LoadResult.Valid && read != LoadResult.Absent)
                {
                    reason = FailureReason(read);
                    return false;
                }
                try { Directory.CreateDirectory(_directory); }
                catch (Exception ex) when (IsIo(ex))
                {
                    reason = "fence-directory-failed";
                    return false;
                }
                return TryWriteAtomic(
                    path,
                    new FenceRecord(accountId, characterId, requestId, payloadHash, Pending),
                    out reason);
            }
        }

        /// <summary>
        /// 只在调用方确定成功或明确完成的失败响应后调用；Timeout/取消/崩溃绝不调用。
        /// 只有现有合法 pending/unknown 且 aid/cid/requestId/hash 完全一致才原子改
        /// resolved；resolved 同参数幂等 true；其它一律拒绝。
        /// </summary>
        internal bool TryResolveKnownTerminal(
            int accountId, int characterId, string requestId,
            string payloadHash, out string reason)
        {
            if (!Validate(accountId, characterId, requestId, payloadHash, out reason)
                || !TryBuildPath(accountId, characterId, out var path))
            {
                reason ??= "fence-path-invalid";
                return false;
            }
            lock (_sync)
            {
                var read = TryLoad(path, accountId, characterId, out var existing);
                if (read != LoadResult.Valid)
                {
                    reason = read == LoadResult.Absent ? "fence-absent" : FailureReason(read);
                    return false;
                }
                if (!string.Equals(existing.RequestId, requestId, StringComparison.Ordinal))
                {
                    reason = "fence-request-mismatch";
                    return false;
                }
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
                {
                    reason = "fence-payload-mismatch";
                    return false;
                }
                if (existing.State == Resolved) { reason = null; return true; }
                // 只允许 pending/unknown → resolved，并保留原 aid/cid/id/hash。
                return TryWriteAtomic(
                    path,
                    new FenceRecord(
                        existing.AccountId, existing.CharacterId,
                        existing.RequestId, existing.PayloadHash, Resolved),
                    out reason);
            }
        }

        private static bool Validate(
            int accountId, int characterId, string requestId,
            string payloadHash, out string reason)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                reason = "fence-invalid-target";
                return false;
            }
            if (!GmAdminRequest.IsValidRequestId(requestId))
            {
                reason = "fence-invalid-request-id";
                return false;
            }
            reason = IsValidHash(payloadHash) ? null : "fence-invalid-payload-hash";
            return reason == null;
        }

        private bool TryBuildPath(int accountId, int characterId, out string path)
        {
            path = null;
            if (accountId <= 0 || characterId <= 0) return false;
            try
            {
                // 路径只由两个正 int 组成，不接受调用方提供的文件名。
                path = Path.Combine(
                    _directory,
                    accountId.ToString(CultureInfo.InvariantCulture) + "-"
                    + characterId.ToString(CultureInfo.InvariantCulture) + ".json");
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        private static bool TryWriteAtomic(
            string targetPath, FenceRecord fence, out string reason)
        {
            // 同目录唯一临时文件，保证 Windows 上 File.Move 不跨卷。
            var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var tempCreated = false;
            try
            {
                var bytes = Serialize(fence);
                using (var stream = new FileStream(
                           tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    tempCreated = true;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(tempPath, targetPath, true);
                tempCreated = false;
                reason = null;
                return true;
            }
            catch (Exception ex) when (IsIo(ex)) { reason = "fence-write-failed"; return false; }
            finally
            {
                if (tempCreated)
                {
                    // 只清理本次创建的那个临时文件，失败不改变返回结果。
                    try { File.Delete(tempPath); }
                    catch (Exception ex) when (IsIo(ex)) { }
                }
            }
        }

        private static byte[] Serialize(FenceRecord fence)
        {
            using var buffer = new MemoryStream(256);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema", 1);
                writer.WriteNumber("targetAccountId", fence.AccountId);
                writer.WriteNumber("targetCharacterId", fence.CharacterId);
                writer.WriteString("requestId", fence.RequestId);
                writer.WriteString("payloadHash", fence.PayloadHash);
                writer.WriteString("state", fence.State);
                writer.WriteString(
                    "updatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }

        /// <summary>
        /// Absent 表示记录不存在（noFence）；其余失败由 FailureReason 映射成固定错误码，
        /// 绝不把权限/IO 错误当成 noFence。严格校验 schema/目标/state/id/hash/updatedAt。
        /// </summary>
        private static LoadResult TryLoad(
            string path, int accountId, int characterId, out FenceRecord fence)
        {
            fence = null;
            byte[] bytes;
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length <= 0 || stream.Length > MaximumRecordBytes)
                    return LoadResult.Corrupt;
                bytes = new byte[(int)stream.Length];
                stream.ReadExactly(bytes);
            }
            catch (FileNotFoundException) { return LoadResult.Absent; }
            catch (DirectoryNotFoundException) { return LoadResult.Absent; }
            catch (Exception ex) when (IsIo(ex)) { return LoadResult.Unreadable; }

            try
            {
                using var document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8,
                    });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return LoadResult.Corrupt;
                // 严格闭集：未知字段、重复字段、缺字段都视为损坏。
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in root.EnumerateObject())
                    if (!Fields.Contains(property.Name) || !seen.Add(property.Name))
                        return LoadResult.Corrupt;
                if (seen.Count != Fields.Count
                    || !TryGetInt32(root, "schema", out var schema) || schema != 1
                    || !TryGetInt32(root, "targetAccountId", out var recordAccountId)
                    || !TryGetInt32(root, "targetCharacterId", out var recordCharacterId))
                    return LoadResult.Corrupt;
                if (recordAccountId != accountId || recordCharacterId != characterId)
                    return LoadResult.TargetMismatch;
                var requestId = GetString(root, "requestId");
                var payloadHash = GetString(root, "payloadHash");
                var state = GetString(root, "state");
                var updatedAt = GetString(root, "updatedAt");
                if (!GmAdminRequest.IsValidRequestId(requestId)
                    || !IsValidHash(payloadHash)
                    || (state != Pending && state != Unknown && state != Resolved)
                    || updatedAt == null
                    || !DateTimeOffset.TryParse(
                        updatedAt,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out _))
                {
                    return LoadResult.Corrupt;
                }
                fence = new FenceRecord(
                    recordAccountId, recordCharacterId, requestId, payloadHash, state);
                return LoadResult.Valid;
            }
            catch (JsonException) { return LoadResult.Corrupt; }
        }

        private static bool IsIo(Exception ex)
            => ex is IOException || ex is UnauthorizedAccessException;

        private static string FailureReason(LoadResult read) => read switch
        {
            LoadResult.TargetMismatch => "fence-target-mismatch",
            LoadResult.Corrupt => "fence-corrupt",
            _ => "fence-unreadable",
        };

        private static bool TryGetInt32(JsonElement root, string name, out int value)
        {
            value = 0;
            return root.TryGetProperty(name, out var element)
                   && element.ValueKind == JsonValueKind.Number
                   && element.TryGetInt32(out value);
        }

        private static string GetString(JsonElement root, string name)
            => root.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        private static bool IsValidHash(string payloadHash)
        {
            if (payloadHash == null || payloadHash.Length != 64) return false;
            foreach (var ch in payloadHash)
            {
                var isDigit = ch >= '0' && ch <= '9';
                var isLowerHex = ch >= 'a' && ch <= 'f';
                var isUpperHex = ch >= 'A' && ch <= 'F';
                if (!isDigit && !isLowerHex && !isUpperHex) return false;
            }
            return true;
        }

        private enum LoadResult { Absent = 0, Valid = 1, Corrupt = 2, TargetMismatch = 3, Unreadable = 4 }

        private sealed class FenceRecord
        {
            internal FenceRecord(
                int accountId, int characterId, string requestId,
                string payloadHash, string state)
            {
                AccountId = accountId;
                CharacterId = characterId;
                RequestId = requestId;
                PayloadHash = payloadHash;
                State = state;
            }
            internal int AccountId { get; }
            internal int CharacterId { get; }
            internal string RequestId { get; }
            internal string PayloadHash { get; }
            internal string State { get; }
        }
    }
}
