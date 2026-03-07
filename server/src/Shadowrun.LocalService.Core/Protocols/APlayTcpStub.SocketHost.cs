using System;
using System.Net.Sockets;
using System.Threading;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        public void Run(ManualResetEvent stopEvent)
        {
            var listener = new TcpListener(ResolveBindAddress(_options.Host), _options.APlayPort);
            listener.Start();
            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay",
                message = string.Format("tcp stub listening on {0}:{1}", _options.Host, _options.APlayPort),
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                stopEvent.WaitOne();
                try { listener.Stop(); }
                catch { }
            });

            while (!stopEvent.WaitOne(0))
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (stopEvent.WaitOne(0))
                    {
                        break;
                    }
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    HandleAcceptedClient(state as TcpClient, stopEvent);
                }, client);
            }
        }

        private void HandleAcceptedClient(TcpClient client, ManualResetEvent stopEvent)
        {
            if (client == null)
            {
                return;
            }

            try
            {
                HandleClient(client, stopEvent);
            }
            catch (Exception ex)
            {
                LogClientWorkerFault(client, ex);
            }
        }

        private void LogClientWorkerFault(TcpClient client, Exception ex)
        {
            var workerPeer = TryGetClientPeer(client);

            try
            {
                Console.Error.WriteLine("[{0}] [aplay-client-worker-fault] peer={1} exception={2}", RequestLogger.UtcNowIso(), workerPeer, ex.Message);
                Console.Error.WriteLine(ex.ToString());
            }
            catch
            {
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "aplay-client-worker-fault",
                peer = workerPeer,
                exception = ex.GetType().FullName,
                message = ex.Message,
                stack = ex.ToString(),
            });
        }

        private static string TryGetClientPeer(TcpClient client)
        {
            try
            {
                if (client != null && client.Client != null && client.Client.RemoteEndPoint != null)
                {
                    return client.Client.RemoteEndPoint.ToString();
                }
            }
            catch
            {
            }

            return "unknown";
        }
    }
}
