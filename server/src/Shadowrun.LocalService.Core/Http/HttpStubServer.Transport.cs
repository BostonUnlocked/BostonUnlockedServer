using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Shadowrun.LocalService.Core.Http
{
    public sealed partial class HttpStubServer
    {
        private const int MaxHttpHeaderBytes = 64 * 1024;
        private const int MaxHttpBodyBytes = 1024 * 1024;

        private static HttpRequest ReadSingleRequest(NetworkStream stream)
        {
            // Read until header terminator (\r\n\r\n). Keep it simple: one request per TCP connection.
            var buffer = new byte[4096];
            var ms = new MemoryStream();
            int headerEnd;

            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return null;
                }
                ms.Write(buffer, 0, read);

                if (TryFindHeaderEnd(ms.GetBuffer(), (int)ms.Length, out headerEnd))
                {
                    break;
                }

                if (ms.Length > MaxHttpHeaderBytes)
                {
                    throw new InvalidOperationException("HTTP header too large");
                }
            }

            var all = ms.ToArray();
            var headerBytes = new byte[headerEnd];
            Buffer.BlockCopy(all, 0, headerBytes, 0, headerEnd);
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (headerLines.Length == 0)
            {
                return null;
            }

            var firstLine = headerLines[0] ?? string.Empty;
            var parts = firstLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "GET";
            var target = parts.Length > 1 ? parts[1] : "/";

            var path = target;
            var query = string.Empty;
            var qm = target.IndexOf('?');
            if (qm >= 0)
            {
                path = target.Substring(0, qm);
                query = target.Substring(qm);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < headerLines.Length; i++)
            {
                var line = headerLines[i];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (!headers.ContainsKey(name))
                {
                    headers[name] = value;
                }
            }

            int contentLength = 0;
            string contentLengthRaw;
            if (headers.TryGetValue("Content-Length", out contentLengthRaw))
            {
                if (!int.TryParse(contentLengthRaw, out contentLength) || contentLength < 0)
                {
                    throw new InvalidOperationException("Invalid Content-Length");
                }
            }

            if (contentLength > MaxHttpBodyBytes)
            {
                throw new InvalidOperationException("HTTP body too large");
            }

            var bodyBytes = new byte[0];
            if (contentLength > 0)
            {
                bodyBytes = new byte[contentLength];
                var already = all.Length - headerEnd;
                if (already > 0)
                {
                    var toCopy = Math.Min(contentLength, already);
                    Buffer.BlockCopy(all, headerEnd, bodyBytes, 0, toCopy);
                    var remaining = contentLength - toCopy;
                    if (remaining > 0)
                    {
                        ReadExact(stream, bodyBytes, toCopy, remaining);
                    }
                }
                else
                {
                    ReadExact(stream, bodyBytes, 0, contentLength);
                }
            }

            string host;
            headers.TryGetValue("Host", out host);
            string userAgent;
            headers.TryGetValue("User-Agent", out userAgent);
            string contentType;
            headers.TryGetValue("Content-Type", out contentType);

            return new HttpRequest
            {
                Method = method,
                Path = path,
                Query = query,
                Host = host,
                UserAgent = userAgent,
                ContentType = contentType,
                BodyBytes = bodyBytes,
            };
        }

        private static void ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            var readTotal = 0;
            while (readTotal < count)
            {
                var read = stream.Read(buffer, offset + readTotal, count - readTotal);
                if (read <= 0)
                {
                    throw new EndOfStreamException("Unexpected EOF while reading HTTP body");
                }
                readTotal += read;
            }
        }

        private static bool TryFindHeaderEnd(byte[] buffer, int length, out int headerEnd)
        {
            // Return index just after \r\n\r\n.
            for (var i = 3; i < length; i++)
            {
                if (buffer[i - 3] == 13 && buffer[i - 2] == 10 && buffer[i - 1] == 13 && buffer[i] == 10)
                {
                    headerEnd = i + 1;
                    return true;
                }
            }
            headerEnd = 0;
            return false;
        }

        private static void WriteResponse(NetworkStream stream, HttpResponse response, bool suppressBody)
        {
            if (response == null)
            {
                response = TextResponse(500, "internal error", "text/plain; charset=utf-8");
            }

            var body = response.BodyBytes ?? new byte[0];
            var filePath = response.BodyFilePath;
            long contentLength = body.LongLength;

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                contentLength = new FileInfo(filePath).Length;
            }

            var statusLine = string.Format("HTTP/1.1 {0} {1}\r\n", response.StatusCode, response.ReasonPhrase ?? "OK");
            var headers = new StringBuilder();
            headers.Append("Server: shadowrun-localservice\r\n");
            headers.Append("Connection: close\r\n");
            headers.Append("Content-Length: ").Append(contentLength).Append("\r\n");
            if (!string.IsNullOrEmpty(response.ContentType))
            {
                headers.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            }
            headers.Append("\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(statusLine + headers);
            stream.Write(headerBytes, 0, headerBytes.Length);

            if (suppressBody)
            {
                return;
            }

            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                using (var file = File.OpenRead(filePath))
                {
                    var chunk = new byte[64 * 1024];
                    while (true)
                    {
                        var read = file.Read(chunk, 0, chunk.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        stream.Write(chunk, 0, read);
                    }
                }
                return;
            }

            if (body.Length > 0)
            {
                stream.Write(body, 0, body.Length);
            }
        }

        private sealed class HttpRequest
        {
            public string Method;
            public string Path;
            public string Query;
            public string Host;
            public string UserAgent;
            public string ContentType;
            public byte[] BodyBytes;
        }

        private sealed class HttpResponse
        {
            public int StatusCode;
            public string ReasonPhrase;
            public string ContentType;
            public byte[] BodyBytes;
            public string BodyFilePath;
        }
    }
}
