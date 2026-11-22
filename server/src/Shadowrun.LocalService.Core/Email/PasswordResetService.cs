using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Shadowrun.LocalService.Core.Persistence;

namespace Shadowrun.LocalService.Core.Email
{
    public sealed class PasswordResetService
    {
        // Unambiguous uppercase alphanumeric alphabet (excludes 0/O and 1/I).
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const int RawCodeLength = 8;

        private readonly PasswordResetEmailConfig _config;
        private readonly IPasswordResetEmailSender _emailSender;
        private readonly LocalUserStore _userStore;
        private readonly ISessionIdentityMap _sessionIdentityMap;
        private readonly RequestLogger _logger;

        private readonly object _rateLimitLock = new object();
        private readonly Dictionary<string, List<DateTime>> _emailRequestHistory = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<DateTime>> _ipRequestHistory = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastRateLimitPruneUtc = DateTime.MinValue;

        public PasswordResetService(
            PasswordResetEmailConfig config,
            IPasswordResetEmailSender emailSender,
            LocalUserStore userStore,
            ISessionIdentityMap sessionIdentityMap,
            RequestLogger logger)
        {
            _config = config ?? new PasswordResetEmailConfig();
            _emailSender = emailSender ?? new ResendPasswordResetEmailSender(_config, logger);
            _userStore = userStore;
            _sessionIdentityMap = sessionIdentityMap;
            _logger = logger;
        }

        public bool IsEnabled
        {
            get { return _emailSender != null && _emailSender.IsEnabled; }
        }

        public string ProviderName
        {
            get { return _emailSender != null ? _emailSender.ProviderName : null; }
        }

        public void RequestPasswordReset(
            string email,
            string remoteIp,
            out int statusCode,
            out string message,
            out bool success)
        {
            statusCode = 500;
            message = "PasswordResetFailed";
            success = false;

            if (!IsEnabled)
            {
                statusCode = 501;
                message = "NotSupported";
                success = false;
                return;
            }

            var normalizedEmail = NormalizeEmail(email);
            if (IsNullOrWhiteSpace(normalizedEmail))
            {
                statusCode = 400;
                message = "InvalidEmail";
                success = false;
                return;
            }

            if (_userStore == null)
            {
                statusCode = 500;
                message = "PasswordResetFailed";
                success = false;
                return;
            }

            var normalizedIp = NormalizeRemoteIp(remoteIp);
            string rateLimitReason;
            if (!TryConsumeRateLimit(normalizedEmail, normalizedIp, out rateLimitReason))
            {
                LogSafe(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "password-reset-rate-limited",
                    reason = rateLimitReason,
                    remoteIp = normalizedIp,
                });

                statusCode = 429;
                message = "TooManyRequests";
                success = false;
                return;
            }

            var resetCode = GenerateResetCode();
            var codeHash = ComputeResetCodeHash(resetCode);
            var ttlMinutes = _config.ResetCodeTtlMinutes > 0
                ? _config.ResetCodeTtlMinutes
                : PasswordResetEmailConfig.DefaultResetCodeTtlMinutes;
            var expiresUtc = DateTime.UtcNow.AddMinutes(ttlMinutes);

            string identityHash;
            string displayName;
            string storeMessage;
            if (!_userStore.TryCreatePasswordResetCode(normalizedEmail, codeHash, expiresUtc, normalizedIp, out identityHash, out displayName, out storeMessage))
            {
                var normalizedMessage = IsNullOrWhiteSpace(storeMessage) ? "AccountNotFound" : storeMessage;
                statusCode = MapFailureStatusCode(normalizedMessage, 401);
                message = normalizedMessage;
                success = false;

                LogSafe(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "password-reset-request-rejected",
                    reason = normalizedMessage,
                    remoteIp = normalizedIp,
                });
                return;
            }

            string sendError;
            if (!_emailSender.TrySendPasswordResetCode(normalizedEmail, displayName, resetCode, ttlMinutes, out sendError))
            {
                try
                {
                    _userStore.TryInvalidatePasswordResetCode(normalizedEmail, codeHash);
                }
                catch
                {
                }

                LogSafe(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "password-reset-request-delivery-failed",
                    accountId = identityHash,
                    provider = ProviderName,
                    error = sendError,
                });

                statusCode = 503;
                message = "EmailDeliveryFailed";
                success = false;
                return;
            }

            LogSafe(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "password-reset-requested",
                accountId = identityHash,
                provider = ProviderName,
                ttlMinutes = ttlMinutes,
                remoteIp = normalizedIp,
            });

            statusCode = 200;
            message = "OK";
            success = true;
        }

        public void ChangePassword(
            string email,
            string resetCode,
            string newPassword,
            string remoteIp,
            out int statusCode,
            out string message,
            out bool success)
        {
            statusCode = 500;
            message = "ChangePasswordFailed";
            success = false;

            var normalizedEmail = NormalizeEmail(email);
            if (IsNullOrWhiteSpace(normalizedEmail))
            {
                statusCode = 400;
                message = "InvalidEmail";
                success = false;
                return;
            }

            if (IsNullOrWhiteSpace(newPassword))
            {
                statusCode = 400;
                message = "InvalidPassword";
                success = false;
                return;
            }

            var normalizedCode = NormalizeResetCode(resetCode);
            if (IsNullOrWhiteSpace(normalizedCode))
            {
                statusCode = 400;
                message = "InvalidResetCode";
                success = false;
                return;
            }

            if (_userStore == null)
            {
                statusCode = 500;
                message = "ChangePasswordFailed";
                success = false;
                return;
            }

            var codeHash = ComputeResetCodeHash(normalizedCode);
            var maxAttempts = _config.MaxVerifyAttempts > 0
                ? _config.MaxVerifyAttempts
                : PasswordResetEmailConfig.DefaultMaxVerifyAttempts;

            string identityHash;
            string storeMessage;
            if (!_userStore.TryChangePasswordWithResetCode(normalizedEmail, codeHash, newPassword, maxAttempts, out identityHash, out storeMessage))
            {
                var normalizedMessage = IsNullOrWhiteSpace(storeMessage) ? "InvalidResetCode" : storeMessage;
                statusCode = MapFailureStatusCode(normalizedMessage, 400);
                message = normalizedMessage;
                success = false;

                LogSafe(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "password-reset-change-failed",
                    accountId = identityHash,
                    reason = normalizedMessage,
                    remoteIp = NormalizeRemoteIp(remoteIp),
                });
                return;
            }

            var invalidatedSessions = 0;
            if (_sessionIdentityMap != null && !IsNullOrWhiteSpace(identityHash))
            {
                try
                {
                    invalidatedSessions = _sessionIdentityMap.RemoveIdentity(identityHash);
                }
                catch
                {
                }
            }

            LogSafe(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "password-reset-completed",
                accountId = identityHash,
                invalidatedSessions = invalidatedSessions,
                remoteIp = NormalizeRemoteIp(remoteIp),
            });

            statusCode = 200;
            message = "OK";
            success = true;
        }

        public static string GenerateResetCode()
        {
            var chars = new char[RawCodeLength];
            var randomBytes = new byte[RawCodeLength];
            var rng = new RNGCryptoServiceProvider();
            rng.GetBytes(randomBytes);

            // CodeAlphabet has length 32 (power of 2), so byte & 31 is completely uniform (no modulo bias).
            for (var i = 0; i < RawCodeLength; i++)
            {
                chars[i] = CodeAlphabet[randomBytes[i] & 31];
            }

            return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
        }

        public static string NormalizeResetCode(string rawCode)
        {
            if (IsNullOrWhiteSpace(rawCode))
            {
                return null;
            }

            var sb = new StringBuilder(rawCode.Length);
            for (var i = 0; i < rawCode.Length; i++)
            {
                var ch = rawCode[i];
                if (char.IsWhiteSpace(ch) || ch == '-')
                {
                    continue;
                }
                sb.Append(char.ToUpperInvariant(ch));
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        public static string ComputeResetCodeHash(string resetCode)
        {
            var normalized = NormalizeResetCode(resetCode);
            if (IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(normalized);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }

        private bool TryConsumeRateLimit(string normalizedEmail, string normalizedIp, out string reason)
        {
            reason = null;
            var now = DateTime.UtcNow;
            var hourCutoff = now.AddHours(-1);

            lock (_rateLimitLock)
            {
                MaybePruneRateLimitsNoThrow(now, hourCutoff);

                List<DateTime> emailHits;
                if (!_emailRequestHistory.TryGetValue(normalizedEmail, out emailHits))
                {
                    emailHits = new List<DateTime>();
                    _emailRequestHistory[normalizedEmail] = emailHits;
                }
                PruneOlderThan(emailHits, hourCutoff);

                if (_config.EmailCooldownSeconds > 0 && emailHits.Count > 0)
                {
                    var lastHit = emailHits[emailHits.Count - 1];
                    if (now.Subtract(lastHit).TotalSeconds < _config.EmailCooldownSeconds)
                    {
                        reason = "email-cooldown";
                        return false;
                    }
                }

                if (_config.MaxRequestsPerEmailPerHour > 0 && emailHits.Count >= _config.MaxRequestsPerEmailPerHour)
                {
                    reason = "email-hourly-limit";
                    return false;
                }

                List<DateTime> ipHits = null;
                if (!IsNullOrWhiteSpace(normalizedIp))
                {
                    if (!_ipRequestHistory.TryGetValue(normalizedIp, out ipHits))
                    {
                        ipHits = new List<DateTime>();
                        _ipRequestHistory[normalizedIp] = ipHits;
                    }
                    PruneOlderThan(ipHits, hourCutoff);

                    if (_config.MaxRequestsPerIpPerHour > 0 && ipHits.Count >= _config.MaxRequestsPerIpPerHour)
                    {
                        reason = "ip-hourly-limit";
                        return false;
                    }
                }

                emailHits.Add(now);
                if (ipHits != null)
                {
                    ipHits.Add(now);
                }

                return true;
            }
        }

        private void MaybePruneRateLimitsNoThrow(DateTime now, DateTime hourCutoff)
        {
            if (now.Subtract(_lastRateLimitPruneUtc).TotalMinutes < 10)
            {
                return;
            }

            _lastRateLimitPruneUtc = now;
            PruneDictionary(_emailRequestHistory, hourCutoff);
            PruneDictionary(_ipRequestHistory, hourCutoff);
        }

        private static void PruneDictionary(Dictionary<string, List<DateTime>> map, DateTime cutoff)
        {
            var emptyKeys = new List<string>();
            foreach (var kvp in map)
            {
                PruneOlderThan(kvp.Value, cutoff);
                if (kvp.Value.Count == 0)
                {
                    emptyKeys.Add(kvp.Key);
                }
            }

            for (var i = 0; i < emptyKeys.Count; i++)
            {
                map.Remove(emptyKeys[i]);
            }
        }

        private static void PruneOlderThan(List<DateTime> timestamps, DateTime cutoff)
        {
            if (timestamps == null || timestamps.Count == 0)
            {
                return;
            }

            var keepFrom = 0;
            while (keepFrom < timestamps.Count && timestamps[keepFrom] < cutoff)
            {
                keepFrom++;
            }

            if (keepFrom > 0)
            {
                timestamps.RemoveRange(0, keepFrom);
            }
        }

        private static int MapFailureStatusCode(string message, int fallback)
        {
            if (string.Equals(message, "AccountNotFound", StringComparison.OrdinalIgnoreCase))
            {
                return 401;
            }

            if (string.Equals(message, "InvalidEmail", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "InvalidPassword", StringComparison.OrdinalIgnoreCase)
                || string.Equals(message, "InvalidResetCode", StringComparison.OrdinalIgnoreCase))
            {
                return 400;
            }

            if (string.Equals(message, "NotSupported", StringComparison.OrdinalIgnoreCase))
            {
                return 501;
            }

            return fallback;
        }

        private static string NormalizeEmail(string email)
        {
            if (IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var trimmed = email.Trim().ToLowerInvariant();
            var at = trimmed.IndexOf('@');
            if (at <= 0 || at >= trimmed.Length - 1)
            {
                return null;
            }

            return trimmed;
        }

        private static string NormalizeRemoteIp(string endpoint)
        {
            if (IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            var trimmed = endpoint.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                var close = trimmed.IndexOf(']');
                if (close > 1)
                {
                    return trimmed.Substring(1, close - 1);
                }
            }

            var colon = trimmed.LastIndexOf(':');
            if (colon > 0 && trimmed.IndexOf(':') == colon)
            {
                return trimmed.Substring(0, colon);
            }

            return trimmed;
        }

        private void LogSafe(object payload)
        {
            if (_logger == null || payload == null)
            {
                return;
            }

            try
            {
                _logger.Log(payload);
            }
            catch
            {
            }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
