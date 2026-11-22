using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Email
{
    public sealed class PasswordResetEmailConfig
    {
        public const string DefaultProvider = "resend";
        public const int DefaultResetCodeTtlMinutes = 30;
        public const int DefaultMaxVerifyAttempts = 5;
        public const int DefaultEmailCooldownSeconds = 120;
        public const int DefaultMaxRequestsPerEmailPerHour = 5;
        public const int DefaultMaxRequestsPerIpPerHour = 15;
        public const int DefaultHttpTimeoutMs = 10000;

        public string Provider { get; set; }
        public string ResendApiKey { get; set; }
        public string ResendApiUrl { get; set; }
        public string FromAddress { get; set; }
        public string Subject { get; set; }
        public int ResetCodeTtlMinutes { get; set; }
        public int MaxVerifyAttempts { get; set; }
        public int EmailCooldownSeconds { get; set; }
        public int MaxRequestsPerEmailPerHour { get; set; }
        public int MaxRequestsPerIpPerHour { get; set; }
        public int HttpTimeoutMs { get; set; }

        public PasswordResetEmailConfig()
        {
            Provider = DefaultProvider;
            ResendApiUrl = "https://api.resend.com/emails";
            Subject = "BostonUnlocked Password Reset Code";
            ResetCodeTtlMinutes = DefaultResetCodeTtlMinutes;
            MaxVerifyAttempts = DefaultMaxVerifyAttempts;
            EmailCooldownSeconds = DefaultEmailCooldownSeconds;
            MaxRequestsPerEmailPerHour = DefaultMaxRequestsPerEmailPerHour;
            MaxRequestsPerIpPerHour = DefaultMaxRequestsPerIpPerHour;
            HttpTimeoutMs = DefaultHttpTimeoutMs;
        }

        public bool IsConfigured
        {
            get
            {
                return !IsNullOrWhiteSpace(Provider)
                    && string.Equals(Provider.Trim(), "resend", StringComparison.OrdinalIgnoreCase)
                    && !IsNullOrWhiteSpace(ResendApiKey)
                    && !IsNullOrWhiteSpace(FromAddress);
            }
        }

        public static PasswordResetEmailConfig Load(LocalServiceOptions options, RequestLogger logger)
        {
            var config = new PasswordResetEmailConfig();
            var configPath = ResolveConfigPath(options);

            if (!IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
            {
                try
                {
                    var raw = File.ReadAllText(configPath);
                    if (!IsNullOrWhiteSpace(raw))
                    {
                        var serializer = new JavaScriptSerializer();
                        var dict = serializer.DeserializeObject(raw) as Dictionary<string, object>;
                        if (dict != null)
                        {
                            ApplyJsonString(dict, "provider", delegate (string v) { config.Provider = v; });
                            ApplyJsonString(dict, "resendApiKey", delegate (string v) { config.ResendApiKey = v; });
                            ApplyJsonString(dict, "resendApiUrl", delegate (string v) { config.ResendApiUrl = v; });
                            ApplyJsonString(dict, "fromAddress", delegate (string v) { config.FromAddress = v; });
                            ApplyJsonString(dict, "subject", delegate (string v) { config.Subject = v; });
                            ApplyJsonInt(dict, "resetCodeTtlMinutes", 1, 1440, delegate (int v) { config.ResetCodeTtlMinutes = v; });
                            ApplyJsonInt(dict, "maxVerifyAttempts", 1, 20, delegate (int v) { config.MaxVerifyAttempts = v; });
                            ApplyJsonInt(dict, "emailCooldownSeconds", 0, 3600, delegate (int v) { config.EmailCooldownSeconds = v; });
                            ApplyJsonInt(dict, "maxRequestsPerEmailPerHour", 1, 100, delegate (int v) { config.MaxRequestsPerEmailPerHour = v; });
                            ApplyJsonInt(dict, "maxRequestsPerIpPerHour", 1, 500, delegate (int v) { config.MaxRequestsPerIpPerHour = v; });
                            ApplyJsonInt(dict, "httpTimeoutMs", 1000, 60000, delegate (int v) { config.HttpTimeoutMs = v; });
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (logger != null)
                    {
                        try
                        {
                            logger.Log(new
                            {
                                ts = RequestLogger.UtcNowIso(),
                                type = "email-config-error",
                                path = configPath,
                                message = ex.Message,
                            });
                        }
                        catch
                        {
                        }
                    }
                }
            }

            // Environment variables override file settings so secrets can be injected cleanly.
            ApplyEnvString("SRO_EMAIL_PROVIDER", delegate (string v) { config.Provider = v; });
            ApplyEnvString("SRO_RESEND_API_KEY", delegate (string v) { config.ResendApiKey = v; });
            ApplyEnvString("SRO_RESEND_API_URL", delegate (string v) { config.ResendApiUrl = v; });
            ApplyEnvString("SRO_EMAIL_FROM", delegate (string v) { config.FromAddress = v; });
            ApplyEnvString("SRO_EMAIL_RESET_SUBJECT", delegate (string v) { config.Subject = v; });
            ApplyEnvInt("SRO_PASSWORD_RESET_CODE_TTL_MINUTES", 1, 1440, delegate (int v) { config.ResetCodeTtlMinutes = v; });
            ApplyEnvInt("SRO_PASSWORD_RESET_MAX_ATTEMPTS", 1, 20, delegate (int v) { config.MaxVerifyAttempts = v; });
            ApplyEnvInt("SRO_PASSWORD_RESET_COOLDOWN_SECONDS", 0, 3600, delegate (int v) { config.EmailCooldownSeconds = v; });
            ApplyEnvInt("SRO_PASSWORD_RESET_MAX_PER_EMAIL_HOUR", 1, 100, delegate (int v) { config.MaxRequestsPerEmailPerHour = v; });
            ApplyEnvInt("SRO_PASSWORD_RESET_MAX_PER_IP_HOUR", 1, 500, delegate (int v) { config.MaxRequestsPerIpPerHour = v; });
            ApplyEnvInt("SRO_EMAIL_HTTP_TIMEOUT_MS", 1000, 60000, delegate (int v) { config.HttpTimeoutMs = v; });

            return config;
        }

        private static string ResolveConfigPath(LocalServiceOptions options)
        {
            if (options == null)
            {
                return null;
            }

            if (!IsNullOrWhiteSpace(options.EmailConfigPath))
            {
                return options.EmailConfigPath;
            }

            if (!IsNullOrWhiteSpace(options.DataDir))
            {
                return Path.Combine(options.DataDir, "email-config.json");
            }

            return null;
        }

        private static void ApplyJsonString(Dictionary<string, object> dict, string key, Action<string> setter)
        {
            if (dict == null || setter == null)
            {
                return;
            }

            foreach (var kvp in dict)
            {
                if (!string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = kvp.Value as string;
                if (!IsNullOrWhiteSpace(value))
                {
                    setter(value.Trim());
                }
                return;
            }
        }

        private static void ApplyJsonInt(Dictionary<string, object> dict, string key, int min, int max, Action<int> setter)
        {
            if (dict == null || setter == null)
            {
                return;
            }

            foreach (var kvp in dict)
            {
                if (!string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int parsed;
                if (TryConvertInt(kvp.Value, out parsed) && parsed >= min && parsed <= max)
                {
                    setter(parsed);
                }
                return;
            }
        }

        private static void ApplyEnvString(string variableName, Action<string> setter)
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(variableName);
                if (!IsNullOrWhiteSpace(raw))
                {
                    setter(raw.Trim());
                }
            }
            catch
            {
            }
        }

        private static void ApplyEnvInt(string variableName, int min, int max, Action<int> setter)
        {
            try
            {
                var raw = Environment.GetEnvironmentVariable(variableName);
                if (IsNullOrWhiteSpace(raw))
                {
                    return;
                }

                int parsed;
                if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    && parsed >= min
                    && parsed <= max)
                {
                    setter(parsed);
                }
            }
            catch
            {
            }
        }

        private static bool TryConvertInt(object value, out int parsed)
        {
            parsed = 0;
            if (value == null)
            {
                return false;
            }

            if (value is int)
            {
                parsed = (int)value;
                return true;
            }

            if (value is long)
            {
                var l = (long)value;
                if (l >= int.MinValue && l <= int.MaxValue)
                {
                    parsed = (int)l;
                    return true;
                }
                return false;
            }

            var text = value as string;
            if (!IsNullOrWhiteSpace(text))
            {
                return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
            }

            try
            {
                parsed = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            return value == null || value.Trim().Length == 0;
        }
    }
}
