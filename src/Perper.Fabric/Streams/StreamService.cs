using System;
using System.IO.Pipelines;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apache.Ignite.Core;
using Apache.Ignite.Core.Binary;
using Apache.Ignite.Core.Resource;
using Apache.Ignite.Core.Services;
using Perper.Protocol.Cache;
using Perper.Protocol.Notifications;

namespace Perper.Fabric.Streams
{
    [Serializable]
    public class StreamService : IService
    {
        public string StreamObjectTypeName { get; set; }

        [InstanceResource] private IIgnite _ignite;
        
        [NonSerialized] private Stream _stream;

        [NonSerialized] private PipeWriter _pipeWriter;

        [NonSerialized] private Task _task;
        [NonSerialized] private CancellationTokenSource _cancellationTokenSource;

        public void Init(IServiceContext context)
        {
            _stream = new Stream(StreamBinaryTypeName.Parse(StreamObjectTypeName), _ignite);

            try
            {
                var clientSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.IP);
                clientSocket.Connect(
                    new UnixDomainSocketEndPoint($"/tmp/perper_{_stream.StreamObjectTypeName.DelegateName}.sock"));

                var networkStream = new NetworkStream(clientSocket);
                _pipeWriter = PipeWriter.Create(networkStream);

            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }

        }

        public void Execute(IServiceContext context)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;
            _task = Task.Run(async () =>
            {
                await Invoke();
                await Task.WhenAll(new[] {InvokeWorker(cancellationToken)}.Union(_stream.GetInputStreams()
                    .Select(inputStream => Engage(inputStream, cancellationToken))));
            }, cancellationToken);
        }

        public void Cancel(IServiceContext context)
        {
            _cancellationTokenSource.Cancel();
            _task.Wait();
        }

        private async ValueTask Invoke()
        {
            await SendNotification(new StreamTriggerNotification());
        }

        private async Task InvokeWorker(CancellationToken cancellationToken)
        {
            var cache = _ignite.GetOrCreateCache<string, IBinaryObject>("workers");
            var workers = cache.GetKeysAsync((s, _) => s == _stream.StreamObjectTypeName.DelegateName,
                cancellationToken);
            await foreach (var batch in workers.WithCancellation(cancellationToken))
            {
                foreach (var _ in batch)
                {
                    await SendNotification(new WorkerTriggerNotification());
                }
            }
        }

        private async Task Engage(Tuple<string, Stream> inputStream, CancellationToken cancellationToken)
        {
            var (parameterName, parameterStream) = inputStream;
            var parameterStreamObjectTypeName = parameterStream.StreamObjectTypeName;
            await foreach (var items in parameterStream.ListenAsync(cancellationToken))
            {
                foreach (var item in items)
                {
                    await SendNotification(new StreamParameterItemUpdateNotification(
                        parameterName,
                        parameterStreamObjectTypeName.DelegateType.ToString(),
                        parameterStreamObjectTypeName.DelegateName,
                        item));
                }
            }
        }

        private async ValueTask SendNotification(object notification)
        {
            var message = notification.ToString();
            var messageBytes = new byte[message.Length + 1];
            messageBytes[0] = (byte) message.Length;
            Encoding.Default.GetBytes(message, 0, message.Length, messageBytes, 1);
            await _pipeWriter.WriteAsync(new ReadOnlyMemory<byte>(messageBytes));
            await _pipeWriter.FlushAsync();
        }
    }
}