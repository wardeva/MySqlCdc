using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MySqlCdc.Constants;
using MySqlCdc.Events;
using MySqlCdc.Packets;
using MySqlCdc.Protocol;

namespace MySqlCdc
{
    /// <summary>
    /// Reads binlog events from a stream.
    /// </summary>
    public class BinlogReader
    {
        private static byte[] MagicNumber = new byte[] { 0xfe, 0x62, 0x69, 0x6e };

        private readonly Channel<IPacket> _channel = Channel.CreateBounded<IPacket>(
            new BoundedChannelOptions(100)
            {
                SingleReader = true,
                SingleWriter = true
            });

        private readonly EventDeserializer _eventDeserializer;
        private readonly PipeReader _pipeReader;

        /// <summary>
        /// Creates a new <see cref="BinlogReader"/>.
        /// </summary>
        /// <param name="eventDeserializer">EventDeserializer implementation for a specific provider</param>
        /// <param name="stream">Stream representing a binlog file</param>
        /// <param name="cancellationToken">Cancellation token</param>
        public BinlogReader(EventDeserializer eventDeserializer, Stream stream, CancellationToken cancellationToken = default)
        {
            byte[] header = new byte[EventConstants.FirstEventPosition];
            stream.Read(header, 0, EventConstants.FirstEventPosition);

            if (!header.SequenceEqual(MagicNumber))
                throw new InvalidOperationException("Invalid binary log file header");

            _eventDeserializer = eventDeserializer;
            _pipeReader = PipeReader.Create(stream);
            _ = Task.Run(async () => await ReceivePacketsAsync(_pipeReader, cancellationToken));
        }

        private async Task ReceivePacketsAsync(PipeReader reader, CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(cancellationToken);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (!cancellationToken.IsCancellationRequested)
                {
                    // We can't calculate packet size without the event header
                    if (buffer.Length < EventConstants.HeaderSize)
                        break;

                    // Make sure the event fits in the buffer
                    var eventHeader = GetEventHeader(buffer);
                    if (buffer.Length < eventHeader.EventLength)
                        break;

                    // Process event and repeat in case there are more event in the buffer
                    await OnReceiveEvent(buffer.Slice(0, eventHeader.EventLength), cancellationToken);
                    buffer = buffer.Slice(buffer.GetPosition(eventHeader.EventLength));
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                    break;
            }

            await reader.CompleteAsync();
            _channel.Writer.Complete();
        }

        private EventHeader GetEventHeader(ReadOnlySequence<byte> buffer)
        {
            using var memoryOwner = new MemoryOwner(buffer.Slice(0, EventConstants.HeaderSize));
            var reader = new PacketReader(memoryOwner.Memory.Span);
            return new EventHeader(ref reader);
        }

        private async Task OnReceiveEvent(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var @event = Deserialize(buffer);
                await _channel.Writer.WriteAsync(@event, cancellationToken);
            }
            catch (Exception e)
            {
                // We stop replication if deserialize throws an exception
                // Since a derived database may end up in an inconsistent state.
                await _channel.Writer.WriteAsync(new ExceptionPacket(e), cancellationToken);
            }
        }

        private IBinlogEvent Deserialize(ReadOnlySequence<byte> buffer)
        {
            using var memoryOwner = new MemoryOwner(buffer);
            var reader = new PacketReader(memoryOwner.Memory.Span);
            return _eventDeserializer.DeserializeEvent(ref reader);
        }

        /// <summary>
        /// Reads an event from binlog stream.
        /// </summary>
        /// <returns>Binlog event instance. Null if there are no more events</returns>
        public async Task<IBinlogEvent> ReadEventAsync(CancellationToken cancellationToken = default)
        {
            await _channel.Reader.WaitToReadAsync(cancellationToken);

            if (!_channel.Reader.TryRead(out IPacket packet))
                return null;

            if (packet is ExceptionPacket exceptionPacket)
                throw new Exception("BinlogReader exception.", exceptionPacket.Exception);

            return packet as IBinlogEvent;
        }
    }
}