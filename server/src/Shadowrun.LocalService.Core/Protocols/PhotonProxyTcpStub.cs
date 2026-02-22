using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using PhotonProxy.ChatAndFriends.Client.DTOs;
using PhotonProxy.Common.ServiceCommunication;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
public sealed partial class PhotonProxyTcpStub
{
    private readonly LocalServiceOptions _options;
    private readonly RequestLogger _logger;
    private readonly LocalUserStore _userStore;
    private readonly ISessionIdentityMap _sessionIdentityMap;

    private readonly ClientSerializer _serializer = new ClientSerializer();

    public PhotonProxyTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore)
        : this(options, logger, userStore, null)
    {
    }

    public PhotonProxyTcpStub(LocalServiceOptions options, RequestLogger logger, LocalUserStore userStore, ISessionIdentityMap sessionIdentityMap)
    {
        _options = options;
        _logger = logger;
        _userStore = userStore;
        _sessionIdentityMap = sessionIdentityMap;
    }

    public void Run(ManualResetEvent stopEvent)
    {
        var listener = new TcpListener(ResolveBindAddress(_options.Host), _options.PhotonPort);
        listener.Start();
        _logger.Log(new
        {
            ts = RequestLogger.UtcNowIso(),
            type = "photon",
            message = string.Format("tcp stub listening on {0}:{1}", _options.Host, _options.PhotonPort),
        });

        ThreadPool.QueueUserWorkItem(delegate
        {
            stopEvent.WaitOne();
            try { listener.Stop(); }
            catch { }
        });

        try
        {
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

                ThreadPool.QueueUserWorkItem(delegate (object state)
                {
                    HandleClient((TcpClient)state, stopEvent);
                }, client);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private void HandleClient(TcpClient client, ManualResetEvent stopEvent)
    {
        using (client)
        {
            var endpoint = client.Client.RemoteEndPoint != null ? client.Client.RemoteEndPoint.ToString() : "unknown";
            client.ReceiveTimeout = 2000;
            client.SendTimeout = 2000;
            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "photon-conn",
                peer = endpoint,
                note = "connected",
            });

            using (var stream = client.GetStream())
            {
                var recvBuffer = new List<byte>();
                var initCallbackSent = false;

                var state = new ConnectionState();

                while (!stopEvent.WaitOne(0))
                {
                    var chunk = ReadChunk(stream);
                    if (chunk.Length <= 0)
                    {
                        break;
                    }

                    _logger.LogLow(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "photon-chunk",
                        peer = endpoint,
                        bytes = chunk.Length,
                        hexPreview = ToHexString(chunk, 0, Math.Min(128, chunk.Length)).ToLowerInvariant(),
                        asciiPreview = Encoding.ASCII.GetString(chunk, 0, Math.Min(160, chunk.Length)),
                    });

                    recvBuffer.AddRange(chunk);

                    while (recvBuffer.Count > 0)
                    {
                        if (recvBuffer[0] == 0xF0)
                        {
                            if (recvBuffer.Count < 5)
                            {
                                break;
                            }

                            var pingRequest = recvBuffer.Take(5).ToArray();
                            recvBuffer.RemoveRange(0, 5);

                            var ticks = (uint)(GetUnixTimeMilliseconds() & 0xFFFFFFFF);
                            var pingResponse = new byte[9];
                            pingResponse[0] = 0xF0;
                            pingResponse[1] = (byte)(ticks >> 24);
                            pingResponse[2] = (byte)(ticks >> 16);
                            pingResponse[3] = (byte)(ticks >> 8);
                            pingResponse[4] = (byte)ticks;
                            Buffer.BlockCopy(pingRequest, 1, pingResponse, 5, 4);

                            if (!SendRaw(stream, endpoint, pingResponse, "ping-response"))
                            {
                                return;
                            }
                            continue;
                        }

                        if (recvBuffer.Count < 7)
                        {
                            break;
                        }

                        if (recvBuffer[0] != 0xFB)
                        {
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "photon-parse",
                                peer = endpoint,
                                note = "unexpected-leading-byte",
                                b = recvBuffer[0],
                            });
                            recvBuffer.RemoveAt(0);
                            continue;
                        }

                        var frameLen = (recvBuffer[1] << 24)
                            | (recvBuffer[2] << 16)
                            | (recvBuffer[3] << 8)
                            | recvBuffer[4];

                        if (frameLen < 7)
                        {
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "photon-parse",
                                peer = endpoint,
                                note = "invalid-frame-length",
                                frameLen,
                            });
                            recvBuffer.RemoveAt(0);
                            continue;
                        }

                        if (recvBuffer.Count < frameLen)
                        {
                            break;
                        }

                        var frame = recvBuffer.Take(frameLen).ToArray();
                        recvBuffer.RemoveRange(0, frameLen);

                        var channel = frame[5];
                        var reliable = frame[6];
                        var payload = new byte[frame.Length - 7];
                        Buffer.BlockCopy(frame, 7, payload, 0, payload.Length);

                        _logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "photon-frame",
                            peer = endpoint,
                            frameLen,
                            channel,
                            reliable,
                            payloadLen = payload.Length,
                            payloadHex = ToHexString(payload, 0, Math.Min(128, payload.Length)).ToLowerInvariant(),
                        });

                        if (payload.Length >= 2 && payload[0] == 0xF3)
                        {
                            _logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "photon-command",
                                peer = endpoint,
                                command = payload[1],
                                payloadLen = payload.Length,
                            });
                        }

                        if (!initCallbackSent && payload.Length >= 2 && payload[0] == 0xF3 && payload[1] == 0x00)
                        {
                            if (!SendPhotonFrame(stream, endpoint, new byte[] { 0xF3, 0x01 }, "init-callback"))
                            {
                                return;
                            }
                            initCallbackSent = true;
                        }

                        if (payload.Length >= 3 && payload[0] == 0xF3 && payload[1] == 0x02)
                        {
                            var opCode = payload[2];

                            byte[] operationResponsePayload;
                            if (opCode == 0x64)
                            {
                                var envelope = ParseServiceEnvelopeRequest(payload);
                                var operationId = envelope != null ? envelope.OperationId : 1;
                                var operationName = envelope != null ? envelope.OperationName : null;

                                byte[] serviceResponseBytes;
                                try
                                {
                                    serviceResponseBytes = BuildSerializedServiceResponse(state, operationId, operationName, envelope != null ? envelope.RequestPayload : null);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Log(new
                                    {
                                        ts = RequestLogger.UtcNowIso(),
                                        type = "photon-op64-error",
                                        peer = endpoint,
                                        operationId,
                                        operationName,
                                        message = ex.Message,
                                    });

                                    serviceResponseBytes = BuildSerializedErrorServiceResponse(operationId, "Unhandled PhotonProxy operation: " + operationName);
                                }

                                operationResponsePayload = BuildOperationResponseWithSingleByteArrayParam(opCode, 100, serviceResponseBytes);

                                _logger.Log(new
                                {
                                    ts = RequestLogger.UtcNowIso(),
                                    type = "photon-op64-response",
                                    peer = endpoint,
                                    operationId,
                                    operationName,
                                    responsePayloadLen = serviceResponseBytes != null ? serviceResponseBytes.Length : 0,
                                    responsePayloadHex = serviceResponseBytes != null ? ToHexString(serviceResponseBytes, 0, Math.Min(128, serviceResponseBytes.Length)).ToLowerInvariant() : string.Empty,
                                });
                            }
                            else
                            {
                                operationResponsePayload = new byte[]
                                {
                                    0xF3,
                                    0x03,
                                    opCode,
                                    0x00,
                                    0x00,
                                    0x2A,
                                    0x00,
                                    0x00,
                                };
                            }

                            if (!SendPhotonFrame(stream, endpoint, operationResponsePayload, string.Format("op-response-{0:x2}", opCode)))
                            {
                                return;
                            }
                        }
                    }
                }
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "photon-conn",
                peer = endpoint,
                note = "closed",
            });
        }
    }

    private bool SendPhotonFrame(NetworkStream stream, string endpoint, byte[] payload, string kind)
    {
        var frame = new byte[7 + payload.Length];
        frame[0] = 0xFB;
        frame[1] = (byte)(frame.Length >> 24);
        frame[2] = (byte)(frame.Length >> 16);
        frame[3] = (byte)(frame.Length >> 8);
        frame[4] = (byte)frame.Length;
        frame[5] = 0x00;
        frame[6] = 0x00;
        Buffer.BlockCopy(payload, 0, frame, 7, payload.Length);

        return SendRaw(stream, endpoint, frame, kind);
    }

    private bool SendRaw(NetworkStream stream, string endpoint, byte[] data, string kind)
    {
        try
        {
            stream.Write(data, 0, data.Length);
            Action<object> log = string.Equals(kind, "ping-response", StringComparison.Ordinal) ? (Action<object>)_logger.LogLow : _logger.Log;

            log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "photon-frame-sent",
                peer = endpoint,
                kind,
                bytes = data.Length,
                hex = ToHexString(data, 0, data.Length).ToLowerInvariant(),
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "photon-error",
                peer = endpoint,
                kind = string.Format("{0}-failed", kind),
                message = ex.Message,
            });
            return false;
        }
    }

    private static byte[] ReadChunk(NetworkStream stream)
    {
        var buffer = new byte[8192];
        try
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                return new byte[0];
            }
            if (read == buffer.Length)
            {
                return buffer;
            }
            var slice = new byte[read];
            Buffer.BlockCopy(buffer, 0, slice, 0, read);
            return slice;
        }
        catch
        {
            return new byte[0];
        }
    }

    private static long GetUnixTimeMilliseconds()
    {
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (long)(DateTime.UtcNow - epoch).TotalMilliseconds;
    }

    private static IPAddress ResolveBindAddress(string host)
    {
        if (string.IsNullOrEmpty(host) || host == "0.0.0.0" || host == "+")
        {
            return IPAddress.Any;
        }
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }
        IPAddress ip;
        if (IPAddress.TryParse(host, out ip))
        {
            return ip;
        }
        return IPAddress.Any;
    }

    private static byte[] HexToBytes(string hex)
    {
        if (hex == null)
        {
            return new byte[0];
        }
        hex = hex.Trim();
        if (hex.Length % 2 != 0)
        {
            throw new ArgumentException("hex must have even length");
        }
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((FromHexNibble(hex[i * 2]) << 4) | FromHexNibble(hex[i * 2 + 1]));
        }
        return bytes;
    }

    private static int FromHexNibble(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        throw new ArgumentException("invalid hex char");
    }

    private static string ToHexString(byte[] bytes, int offset, int count)
    {
        if (bytes == null)
        {
            return string.Empty;
        }
        var sb = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++)
        {
            var b = bytes[offset + i];
            sb.Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

}
}
