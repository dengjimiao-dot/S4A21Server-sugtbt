using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Game.Administration
{
    /// <summary>
    /// 管理侧 Named Pipe 宿主。
    ///
    /// 硬约束：
    /// - 只有 <c>AUCTION_ADMIN_PIPE_ENABLED=1</c> 才启动；默认不存在监听面。
    /// - 管道名固定 <see cref="PipeName"/>，PipeOptions.CurrentUserOnly，
    ///   不监听 TCP、不开端口、不做动态注入。
    /// - 单连接、单行 UTF8 JSON、64 KiB 上限、10 秒超时。
    /// - 全部 IO 在后台任务里，绝不阻塞游戏线程；<see cref="Dispose"/>
    ///   取消 accept 并等待循环退出。
    /// </summary>
    internal sealed class GmAdminPipeHost : IDisposable
    {
        internal const string PipeName = "DfoServer.A21.Auction.v1";
        internal const string EnableEnvironmentVariable =
            "AUCTION_ADMIN_PIPE_ENABLED";

        internal const int MaximumLineBytes =
            GmAdminRequest.MaximumRequestBytes;

        internal static readonly TimeSpan ReadTimeout =
            TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan WriteTimeout =
            TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan ShutdownTimeout =
            TimeSpan.FromSeconds(5);

        private readonly GmAdminDispatcher _dispatcher;
        private readonly CancellationTokenSource _shutdown =
            new CancellationTokenSource();
        private readonly object _lifecycleLock = new object();
        private Task _acceptLoop;
        private NamedPipeServerStream _activePipe;
        private int _disposed;

        internal GmAdminPipeHost(GmAdminDispatcher dispatcher)
        {
            _dispatcher = dispatcher
                ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        internal string Name => PipeName;

        internal bool IsRunning
        {
            get
            {
                lock (_lifecycleLock)
                    return _acceptLoop != null && !_acceptLoop.IsCompleted;
            }
        }

        /// <summary>
        /// 启动门。未设置 <c>AUCTION_ADMIN_PIPE_ENABLED=1</c> 时返回 false
        /// 且不创建任何 listener。
        /// </summary>
        internal static bool IsEnabledByEnvironment()
        {
            var value = Environment.GetEnvironmentVariable(
                EnableEnvironmentVariable);
            return string.Equals(
                value == null ? null : value.Trim(),
                "1",
                StringComparison.Ordinal);
        }

        internal bool TryStart()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            // NamedPipeServerStream 在非 Windows 上语义不同，管理面只在
            // Windows 启用；其它平台保持关闭而不是降级到别的传输。
            if (!OperatingSystem.IsWindows())
                return false;

            if (!IsEnabledByEnvironment())
                return false;

            lock (_lifecycleLock)
            {
                if (_acceptLoop != null)
                    return !_acceptLoop.IsCompleted;

                _acceptLoop = Task.Run(
                    () => AcceptLoopAsync(_shutdown.Token));
                return true;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreateServerStream();
                    lock (_lifecycleLock)
                        _activePipe = pipe;

                    // accept 不设短超时：管道名要持续可连，否则外部每次
                    // 重连都可能撞上实例重建的空窗。退出只由 Dispose 的
                    // 取消令牌和显式 Dispose(pipe) 驱动。
                    await pipe.WaitForConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (cancellationToken.IsCancellationRequested)
                        break;

                    await ServeOnceAsync(pipe, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    // 客户端在握手/写入阶段断开：单连接语义下直接回到 accept，
                    // 不把异常抛到宿主线程。
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                finally
                {
                    lock (_lifecycleLock)
                    {
                        if (ReferenceEquals(_activePipe, pipe))
                            _activePipe = null;
                    }

                    try
                    {
                        pipe?.Dispose();
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        private static NamedPipeServerStream CreateServerStream()
        {
            return new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }

        private async Task ServeOnceAsync(
            NamedPipeServerStream pipe,
            CancellationToken cancellationToken)
        {
            // 每连接一次请求/一次响应。多行不会被拼接，超限行直接拒绝。
            GmReadLine line;
            try
            {
                line = await ReadLineAsync(pipe, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            GmAdminResponse response;
            if (line.Status == GmLineStatus.TooLarge)
            {
                response = GmAdminResponse.Fail(
                    "request-too-large",
                    "request exceeds 64 KiB limit");
            }
            else if (line.Status != GmLineStatus.Complete)
            {
                response = GmAdminResponse.Fail(
                    "request-invalid",
                    "no complete UTF8 JSON line received within timeout");
            }
            else if (!GmAdminRequest.TryParse(
                         line.Text,
                         out var request,
                         out var parseError))
            {
                response = GmAdminResponse.Fail(
                    parseError ?? "request-invalid",
                    "request rejected before dispatch");
            }
            else
            {
                response = await _dispatcher
                    .DispatchAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await WriteResponseAsync(
                        pipe,
                        response.ToJson(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>
        /// 读取一行 UTF8。<see cref="GmReadLine.Status"/> 为 Complete 时
        /// <see cref="GmReadLine.Text"/> 是完整一行；TooLarge 表示超过
        /// 64 KiB 上限（不再继续缓冲）；Incomplete 表示超时或对端未发完。
        /// </summary>
        private static async Task<GmReadLine> ReadLineAsync(
            NamedPipeServerStream pipe,
            CancellationToken cancellationToken)
        {
            // 按字节累积再一次性解码：逐字节 (char) 强转会把多字节 UTF-8
            // 拆成非法代理项，这里是唯一正确的做法。
            var pending = new MemoryStream(512);
            var buffer = new byte[4096];
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            var token = timeout.Token;

            while (true)
            {
                int read;
                try
                {
                    read = await pipe
                        .ReadAsync(buffer, 0, buffer.Length, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                        when (!cancellationToken.IsCancellationRequested)
                {
                    return GmReadLine.Incomplete;
                }

                if (read <= 0)
                    return GmReadLine.Incomplete;

                for (var i = 0; i < read; i++)
                {
                    var value = buffer[i];
                    if (value == (byte)'\n')
                    {
                        var bytes = pending.ToArray();
                        var length = bytes.Length;
                        if (length > 0 && bytes[length - 1] == (byte)'\r')
                            length--;

                        try
                        {
                            return GmReadLine.Completed(
                                Encoding.UTF8.GetString(bytes, 0, length));
                        }
                        catch (ArgumentException)
                        {
                            return GmReadLine.Incomplete;
                        }
                    }

                    // 越限即拒绝，避免用超长行拖住管道。
                    if (pending.Length + 1 > MaximumLineBytes)
                        return GmReadLine.TooLarge;

                    pending.WriteByte(value);
                }
            }
        }

        private static async Task WriteResponseAsync(
            NamedPipeServerStream pipe,
            string json,
            CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            timeout.CancelAfter(WriteTimeout);
            await pipe
                .WriteAsync(bytes, 0, bytes.Length, timeout.Token)
                .ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Task loop;
            NamedPipeServerStream active;
            lock (_lifecycleLock)
            {
                loop = _acceptLoop;
                active = _activePipe;
                _activePipe = null;
            }

            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // 取消只中断 WaitForConnectionAsync/ReadAsync；显式 Dispose
            // 管道让处于 blocking accept 的实现也能立即返回。
            try
            {
                active?.Dispose();
            }
            catch (IOException)
            {
            }

            try
            {
                loop?.Wait(ShutdownTimeout);
            }
            catch (AggregateException)
            {
            }

            _shutdown.Dispose();
        }
    }

    internal enum GmLineStatus
    {
        Incomplete = 0,
        Complete = 1,
        TooLarge = 2,
    }

    /// <summary>
    /// 单行读取结果。用显式状态而不是“魔法字符串”区分空行、超限和超时。
    /// </summary>
    internal readonly struct GmReadLine
    {
        private GmReadLine(GmLineStatus status, string text)
        {
            Status = status;
            Text = text;
        }

        internal GmLineStatus Status { get; }

        internal string Text { get; }

        internal static GmReadLine Incomplete
            => new GmReadLine(GmLineStatus.Incomplete, null);

        internal static GmReadLine TooLarge
            => new GmReadLine(GmLineStatus.TooLarge, null);

        internal static GmReadLine Completed(string text)
            => new GmReadLine(GmLineStatus.Complete, text ?? string.Empty);
    }
}
