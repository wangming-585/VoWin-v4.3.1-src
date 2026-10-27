using System;
using System.IO;
using System.Threading.Channels;

namespace VoWin.Services
{
    /// <summary>
    /// 进程内 PCM 管道：替代"子进程 + 命名管道"的跨进程传音方案。
    /// 写入端非阻塞入队，读取端阻塞等待，语义与 NamedPipeStream 一致，
    /// 但不经过内核对象，也不产生额外进程。
    /// </summary>
    internal sealed class InMemoryPcmPipe : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        private byte[]? _pending;
        private int _pendingOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (count <= 0) return 0;

            while (true)
            {
                if (_pending is not null && _pendingOffset < _pending.Length)
                {
                    var n = Math.Min(count, _pending.Length - _pendingOffset);
                    Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset, n);
                    _pendingOffset += n;
                    if (_pendingOffset >= _pending.Length)
                    {
                        _pending = null;
                        _pendingOffset = 0;
                    }
                    return n;
                }

                if (!_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    return 0;

                if (_channel.Reader.TryRead(out var frame))
                {
                    _pending = frame;
                    _pendingOffset = 0;
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            _channel.Writer.TryWrite(copy);
        }

        /// <summary>关闭写端，使阻塞中的 Read 返回 0（用于停止音频泵）。</summary>
        public void CompleteWriter() => _channel.Writer.TryComplete();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _channel.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }
}
