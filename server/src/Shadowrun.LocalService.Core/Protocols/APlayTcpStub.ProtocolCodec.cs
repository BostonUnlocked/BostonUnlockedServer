using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private static bool LooksLikeHttp(byte[] bytes)
        {
            return StartsWithAscii(bytes, "GET ") || StartsWithAscii(bytes, "POST ") || StartsWithAscii(bytes, "HEAD ");
        }

        private static bool StartsWithAscii(byte[] bytes, string prefix)
        {
            if (bytes == null || prefix == null)
            {
                return false;
            }
            var p = Encoding.ASCII.GetBytes(prefix);
            return StartsWith(bytes, p);
        }

        private static bool StartsWith(byte[] bytes, byte[] prefix)
        {
            if (bytes == null || prefix == null || bytes.Length < prefix.Length)
            {
                return false;
            }
            for (var i = 0; i < prefix.Length; i++)
            {
                if (bytes[i] != prefix[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryExtractNullTerminatedFrame(List<byte> buffer, out byte[] frame)
        {
            var nullIndex = buffer.IndexOf(0);
            if (nullIndex < 0)
            {
                frame = new byte[0];
                return false;
            }

            frame = buffer.Take(nullIndex).ToArray();
            buffer.RemoveRange(0, nullIndex + 1);
            return true;
        }

        private static byte[] ReadChunk(NetworkStream stream)
        {
            var buffer = new byte[4096];
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

        private static byte[] PrefixLength(byte[] payload)
        {
            return Concat(BitConverter.GetBytes(payload.Length), payload);
        }

        private static byte[] BuildCoreDirectSystem(uint serverId, byte[] raw, ulong msgNo)
        {
            return Concat(new byte[] { 0x02 }, BitConverter.GetBytes(serverId), BitConverter.GetBytes(raw.Length), raw, BitConverter.GetBytes(msgNo));
        }

        private static byte[] BuildApSharedFieldEvent(byte apMsgId, ulong entityId, ushort fieldId, byte[] data)
        {
            return Concat(new[] { apMsgId }, BitConverter.GetBytes(entityId), BitConverter.GetBytes(fieldId), BitConverter.GetBytes(data.Length), data);
        }

        private static byte[] BuildApSharedEntitySetOwner(ulong entityId, ushort typeId)
        {
            return Concat(new byte[] { 7 }, BitConverter.GetBytes(entityId), BitConverter.GetBytes(typeId));
        }

        private static byte[] BuildGameClientWelcomePayload(ulong accountRefId, string careerSummary)
        {
            return Concat(BitConverter.GetBytes(accountRefId), BuildUtf16StringPayload(careerSummary));
        }

        private static byte[] BuildAccountWelcomePayload(int index, string zippedCareerInfo, ulong metaGameplayRef)
        {
            return Concat(BitConverter.GetBytes(index), BitConverter.GetBytes(metaGameplayRef), BuildUtf16StringPayload(zippedCareerInfo), BuildApDatePayload(DateTimeOffset.UtcNow));
        }

        private static byte[] BuildMetaHubPushPayload(ulong hubRefId, string serializedState)
        {
            return Concat(BitConverter.GetBytes(hubRefId), BuildUtf16StringPayload(serializedState));
        }

        private static byte[] BuildApDatePayload(DateTimeOffset dt)
        {
            var utc = dt.ToUniversalTime();
            return Concat(
                new[] { (byte)utc.Day },
                new[] { (byte)utc.Month },
                BitConverter.GetBytes((ushort)utc.Year),
                new[] { (byte)utc.Hour },
                new[] { (byte)utc.Minute },
                new[] { (byte)utc.Second },
                BitConverter.GetBytes((ushort)utc.Millisecond));
        }

        private static string BuildCareerSummaryJson(List<CareerSlot> careers)
        {
            var slots = new Dictionary<int, CareerSlot>();
            if (careers != null)
            {
                for (var i = 0; i < careers.Count; i++)
                {
                    var s = careers[i];
                    if (s == null)
                    {
                        continue;
                    }
                    slots[s.Index] = s;
                }
            }

            var sb = new StringBuilder();
            sb.Append("[");
            for (var idx = 0; idx < 6; idx++)
            {
                CareerSlot s;
                if (!slots.TryGetValue(idx, out s) || s == null)
                {
                    s = new CareerSlot();
                    s.Index = idx;
                    s.IsOccupied = false;
                    s.CharacterName = string.Empty;
                    s.Portrait = string.Empty;
                }

                var portrait = s.Portrait;
                if (IsNullOrWhiteSpace(portrait))
                {
                    portrait = s.PortraitPath;
                }
                if (IsNullOrWhiteSpace(portrait) && s.IsOccupied)
                {
                    portrait = PlayerCharacterDefaultValues.PortraitPath;
                }

                if (idx > 0)
                {
                    sb.Append(",");
                }

                sb.Append("{\"Name\":\"");
                sb.Append(JsonEscape(s.CharacterName));
                sb.Append("\",\"Portrait\":\"");
                sb.Append(JsonEscape(portrait));
                sb.Append("\",\"Index\":");
                sb.Append(idx.ToString());
                sb.Append(",\"IsOccupied\":");
                sb.Append(s.IsOccupied ? "true" : "false");
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string BuildDefaultCareerSummary()
        {
            return BuildCareerSummaryJson(null);
        }

        private static string JsonEscape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static int? ParseInt32Payload(byte[] data)
        {
            if (data == null || data.Length < 4)
            {
                return null;
            }
            return ReadInt32LE(data, 0);
        }

        private static List<string> ParseUtf16StringPayload(byte[] data)
        {
            var output = new List<string>();
            var pos = 0;
            while (pos + 4 <= data.Length && output.Count < 8)
            {
                var strlen = ReadInt32LE(data, pos);
                pos += 4;
                if (strlen < 0)
                {
                    break;
                }

                var remaining = data.Length - pos;
                if (strlen > (remaining / 2))
                {
                    break;
                }

                var byteLenLong = (long)strlen * 2L;
                if (byteLenLong < 0 || byteLenLong > int.MaxValue)
                {
                    break;
                }

                var byteLen = (int)byteLenLong;
                if (byteLen == 0)
                {
                    output.Add(string.Empty);
                    continue;
                }

                output.Add(Encoding.Unicode.GetString(data, pos, byteLen));
                pos += byteLen;
            }
            return output;
        }

        private static bool TryParsePrepareMatchPayload(
            byte[] data,
            out string matchIdentifier,
            out string players,
            out bool coop,
            out string mapName,
            out string selectedHenchmen)
        {
            matchIdentifier = null;
            players = null;
            coop = false;
            mapName = null;
            selectedHenchmen = null;

            if (data == null || data.Length < 13)
            {
                return false;
            }

            var pos = 0;
            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out matchIdentifier))
            {
                return false;
            }

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out players))
            {
                return false;
            }

            if (pos >= data.Length)
            {
                return false;
            }

            coop = data[pos++] != 0;

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out mapName))
            {
                return false;
            }

            if (!TryReadUtf16LengthPrefixedString(data, ref pos, out selectedHenchmen))
            {
                return false;
            }

            return true;
        }

        private static bool TryReadUtf16LengthPrefixedString(byte[] data, ref int pos, out string value)
        {
            value = null;
            if (data == null || pos < 0 || pos + 4 > data.Length)
            {
                return false;
            }

            var strlen = ReadInt32LE(data, pos);
            pos += 4;
            if (strlen < 0)
            {
                return false;
            }

            var remaining = data.Length - pos;
            if (strlen > (remaining / 2))
            {
                return false;
            }

            var byteLenLong = (long)strlen * 2L;
            if (byteLenLong < 0 || byteLenLong > int.MaxValue)
            {
                return false;
            }

            var byteLen = (int)byteLenLong;
            value = byteLen == 0 ? string.Empty : Encoding.Unicode.GetString(data, pos, byteLen);
            pos += byteLen;
            return true;
        }

        private static byte[] BuildUtf16StringPayload(params string[] values)
        {
            var chunks = new List<byte[]>();
            foreach (var value in values)
            {
                var encoded = Encoding.Unicode.GetBytes(value ?? string.Empty);
                chunks.Add(BitConverter.GetBytes(encoded.Length / 2));
                chunks.Add(encoded);
            }
            return Concat(chunks.ToArray());
        }

        private static CoreDirectSystem? ParseCoreDirectSystem(byte[] corePayload)
        {
            if (corePayload.Length < 17 || corePayload[0] != 3)
            {
                return null;
            }

            var pos = 1;
            uint serverId;
            int rawLen;
            ulong msgNo;
            if (!TryReadUInt32LE(corePayload, ref pos, out serverId)) return null;
            if (!TryReadInt32LE(corePayload, ref pos, out rawLen)) return null;
            if (rawLen < 0 || pos + rawLen + 8 > corePayload.Length) return null;

            var raw = new byte[rawLen];
            Buffer.BlockCopy(corePayload, pos, raw, 0, rawLen);
            pos += rawLen;
            if (!TryReadUInt64LE(corePayload, ref pos, out msgNo)) return null;
            return new CoreDirectSystem(serverId, raw, msgNo);
        }

        private static ApSharedFieldEvent? ParseApSharedFieldEvent(byte[] raw)
        {
            if (raw.Length < 15)
            {
                return null;
            }

            var pos = 0;
            var apMsgId = raw[pos++];
            ulong entityId;
            ushort fieldId;
            int dataLen;
            if (!TryReadUInt64LE(raw, ref pos, out entityId)) return null;
            if (!TryReadUInt16LE(raw, ref pos, out fieldId)) return null;
            if (!TryReadInt32LE(raw, ref pos, out dataLen)) return null;
            if (dataLen < 0 || pos + dataLen > raw.Length) return null;
            var data = new byte[dataLen];
            Buffer.BlockCopy(raw, pos, data, 0, dataLen);
            return new ApSharedFieldEvent(apMsgId, entityId, fieldId, data);
        }

        private static bool TryReadInt32LE(byte[] data, ref int offset, out int value)
        {
            if (offset + 4 > data.Length)
            {
                value = 0;
                return false;
            }
            value = ReadInt32LE(data, offset);
            offset += 4;
            return true;
        }

        private static bool TryReadUInt16LE(byte[] data, ref int offset, out ushort value)
        {
            if (offset + 2 > data.Length)
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt16(data, offset);
            offset += 2;
            return true;
        }

        private static bool TryReadUInt32LE(byte[] data, ref int offset, out uint value)
        {
            if (offset + 4 > data.Length)
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt32(data, offset);
            offset += 4;
            return true;
        }

        private static bool TryReadUInt64LE(byte[] data, ref int offset, out ulong value)
        {
            if (offset + 8 > data.Length)
            {
                value = 0;
                return false;
            }
            value = BitConverter.ToUInt64(data, offset);
            offset += 8;
            return true;
        }

        private static bool TryReadGameClientRef(byte[] data, int offset, out ushort refType, out ulong refId, out int nextOffset)
        {
            refType = 0;
            refId = 0;
            nextOffset = offset;
            if (data == null || offset + 10 > data.Length)
            {
                return false;
            }
            refType = BitConverter.ToUInt16(data, offset);
            refId = BitConverter.ToUInt64(data, offset + 2);
            nextOffset = offset + 10;
            return true;
        }

        private static int ReadInt32LE(byte[] data, int offset)
        {
            return BitConverter.ToInt32(data, offset);
        }

        private static byte[] Concat(params byte[][] chunks)
        {
            var total = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                total += chunks[i] != null ? chunks[i].Length : 0;
            }
            var result = new byte[total];
            var offset = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                if (chunk == null || chunk.Length == 0)
                {
                    continue;
                }
                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }
            return result;
        }

        private static void SleepWithStop(ManualResetEvent stopEvent, int milliseconds)
        {
            var remaining = milliseconds;
            while (remaining > 0 && !stopEvent.WaitOne(0))
            {
                var step = Math.Min(200, remaining);
                Thread.Sleep(step);
                remaining -= step;
            }
        }

        private static bool PayloadContains(List<string> strings, string needle)
        {
            if (strings == null || needle == null)
            {
                return false;
            }
            for (var i = 0; i < strings.Count; i++)
            {
                var s = strings[i] ?? string.Empty;
                if (s.IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string TryExtractUtf16JsonObject(byte[] data)
        {
            if (data == null || data.Length < 2)
            {
                return null;
            }

            var start = -1;
            for (var i = 0; i + 1 < data.Length; i++)
            {
                if (data[i] == 0x7B && data[i + 1] == 0x00)
                {
                    start = i;
                    break;
                }
            }
            if (start < 0)
            {
                return null;
            }

            var len = data.Length - start;
            if ((len % 2) != 0)
            {
                len--;
            }
            if (len <= 0)
            {
                return null;
            }

            var s = Encoding.Unicode.GetString(data, start, len);
            if (IsNullOrWhiteSpace(s))
            {
                return null;
            }

            var end = s.LastIndexOf('}');
            if (end >= 0)
            {
                s = s.Substring(0, end + 1);
            }
            s = s.Trim('\0', ' ', '\r', '\n', '\t');
            return s;
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            if (IsNullOrWhiteSpace(json) || IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var pattern = "\"" + key + "\"";
            var idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }

            idx = json.IndexOf(':', idx);
            if (idx < 0)
            {
                return null;
            }

            idx++;
            while (idx < json.Length && char.IsWhiteSpace(json[idx]))
            {
                idx++;
            }
            if (idx >= json.Length)
            {
                return null;
            }

            if (json[idx] == '"')
            {
                var end = json.IndexOf('"', idx + 1);
                if (end < 0)
                {
                    return null;
                }
                return json.Substring(idx + 1, end - idx - 1);
            }

            var start = idx;
            while (idx < json.Length)
            {
                var ch = json[idx];
                if (ch == ',' || ch == '}' || ch == ']')
                {
                    break;
                }
                if (char.IsWhiteSpace(ch))
                {
                    break;
                }
                idx++;
            }
            if (idx <= start)
            {
                return null;
            }
            return json.Substring(start, idx - start).Trim();
        }

        private static string SafeGetIdentifierExtension(PlayerCharacterSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            var id = snapshot.CharacterIdentifier;
            if (IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var idx = id.IndexOf(':');
            if (idx < 0 || idx + 1 >= id.Length)
            {
                return null;
            }

            return id.Substring(idx + 1);
        }

        private static bool IsPrologueMissionName(string missionName)
        {
            if (IsNullOrWhiteSpace(missionName))
            {
                return false;
            }

            var trimmed = missionName.Trim();
            if (trimmed.IndexOf("Prologue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (string.Equals(trimmed, "1_010_Prologue", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (string.Equals(trimmed, "S010_Prologue", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static bool TryParseInt32(string value, out int result)
        {
            result = 0;
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool TryParseUInt64(string value, out ulong result)
        {
            result = 0UL;
            if (IsNullOrWhiteSpace(value))
            {
                return false;
            }
            return ulong.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }

        private static bool ContainsGuid(Guid[] values, Guid value)
        {
            if (values == null)
            {
                return false;
            }
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == value)
                {
                    return true;
                }
            }
            return false;
        }

        private static Guid[] OrderGuidsWithLeaderFirst(Guid[] values, Guid leader)
        {
            if (values == null || values.Length <= 1)
            {
                return values;
            }
            if (leader == Guid.Empty)
            {
                return values;
            }

            var leaderIndex = -1;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] == leader)
                {
                    leaderIndex = i;
                    break;
                }
            }
            if (leaderIndex <= 0)
            {
                return values;
            }

            var ordered = new Guid[values.Length];
            ordered[0] = leader;
            var writeIdx = 1;
            for (var i = 0; i < values.Length; i++)
            {
                if (i == leaderIndex)
                {
                    continue;
                }
                ordered[writeIdx++] = values[i];
            }
            return ordered;
        }

        private static Guid[] ParseGuidsFromLooseText(string text, int max)
        {
            if (IsNullOrWhiteSpace(text) || max <= 0)
            {
                return new Guid[0];
            }

            var list = new List<Guid>();
            var s = text.Trim();
            for (var i = 0; i + 36 <= s.Length; i++)
            {
                if (s[i + 8] != '-' || s[i + 13] != '-' || s[i + 18] != '-' || s[i + 23] != '-')
                {
                    continue;
                }

                var candidate = s.Substring(i, 36);
                try
                {
                    var g = new Guid(candidate);
                    var exists = false;
                    for (var j = 0; j < list.Count; j++)
                    {
                        if (list[j] == g)
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        list.Add(g);
                        if (list.Count >= max)
                        {
                            break;
                        }
                    }
                    i += 35;
                }
                catch
                {
                }
            }

            return list.ToArray();
        }

        private sealed class GuidStringOrdinalComparer : IComparer<Guid>
        {
            public static readonly GuidStringOrdinalComparer Instance = new GuidStringOrdinalComparer();

            public int Compare(Guid x, Guid y)
            {
                return string.CompareOrdinal(x.ToString(), y.ToString());
            }
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
                sb.Append(bytes[offset + i].ToString("x2"));
            }
            return sb.ToString();
        }

        private struct CoreDirectSystem
        {
            public readonly uint ServerId;
            public readonly byte[] Raw;
            public readonly ulong MsgNo;

            public CoreDirectSystem(uint serverId, byte[] raw, ulong msgNo)
            {
                ServerId = serverId;
                Raw = raw;
                MsgNo = msgNo;
            }
        }

        private struct ApSharedFieldEvent
        {
            public readonly byte ApMsgId;
            public readonly ulong EntityId;
            public readonly ushort FieldId;
            public readonly byte[] Data;

            public ApSharedFieldEvent(byte apMsgId, ulong entityId, ushort fieldId, byte[] data)
            {
                ApMsgId = apMsgId;
                EntityId = entityId;
                FieldId = fieldId;
                Data = data;
            }
        }

        private sealed class CoopMissionParticipant
        {
            public readonly string Peer;
            public readonly NetworkStream Stream;
            public readonly string IdentityHash;
            public readonly Guid IdentityGuid;
            public readonly int CareerIndex;

            public CoopMissionParticipant(string peer, NetworkStream stream, string identityHash, Guid identityGuid, int careerIndex)
            {
                Peer = peer;
                Stream = stream;
                IdentityHash = identityHash;
                IdentityGuid = identityGuid;
                CareerIndex = careerIndex;
            }
        }
    }
}
