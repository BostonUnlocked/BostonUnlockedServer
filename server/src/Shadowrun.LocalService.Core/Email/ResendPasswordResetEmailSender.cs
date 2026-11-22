using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;

namespace Shadowrun.LocalService.Core.Email
{
    public sealed class ResendPasswordResetEmailSender : IPasswordResetEmailSender
    {
        private const SecurityProtocolType Tls12Protocol = (SecurityProtocolType)3072;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private readonly PasswordResetEmailConfig _config;
        private readonly RequestLogger _logger;

        public ResendPasswordResetEmailSender(PasswordResetEmailConfig config, RequestLogger logger)
        {
            _config = config ?? new PasswordResetEmailConfig();
            _logger = logger;
        }

        public bool IsEnabled
        {
            get { return _config != null && _config.IsConfigured; }
        }

        public string ProviderName
        {
            get { return "resend"; }
        }

        public bool TrySendPasswordResetCode(string toEmail, string displayName, string resetCode, int ttlMinutes, out string errorMessage)
        {
            errorMessage = null;

            if (!IsEnabled)
            {
                errorMessage = "Email sender is not configured.";
                return false;
            }

            if (IsNullOrWhiteSpace(toEmail) || IsNullOrWhiteSpace(resetCode))
            {
                errorMessage = "Invalid email or reset code.";
                return false;
            }

            try
            {
                // Ensure TLS 1.2 is enabled on .NET Framework 4.8 before calling Resend over HTTPS.
                ServicePointManager.SecurityProtocol |= Tls12Protocol;
            }
            catch
            {
            }

            var endpoint = !IsNullOrWhiteSpace(_config.ResendApiUrl)
                ? _config.ResendApiUrl
                : "https://api.resend.com/emails";
            var subject = !IsNullOrWhiteSpace(_config.Subject)
                ? _config.Subject
                : "BostonUnlocked Password Reset Code";

            var payload = new Dictionary<string, object>
            {
                { "from", _config.FromAddress },
                { "to", new string[] { toEmail } },
                { "subject", subject },
                { "text", BuildPlainTextBody(displayName, resetCode, ttlMinutes) },
                { "html", BuildHtmlBody(displayName, resetCode, ttlMinutes) },
            };

            var bodyBytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(endpoint);
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Accept = "application/json";
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + _config.ResendApiKey;
                request.Timeout = _config.HttpTimeoutMs > 0 ? _config.HttpTimeoutMs : PasswordResetEmailConfig.DefaultHttpTimeoutMs;
                request.ReadWriteTimeout = request.Timeout;
                request.ServicePoint.Expect100Continue = false;
                request.ContentLength = bodyBytes.Length;

                using (var reqStream = request.GetRequestStream())
                {
                    reqStream.Write(bodyBytes, 0, bodyBytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var responseBody = ReadStreamSafe(response.GetResponseStream());
                    var messageId = TryExtractId(responseBody);

                    LogSafe(new
                    {
                        ts = RequestLogger.UtcNowIso(),
                        type = "password-reset-email-sent",
                        provider = ProviderName,
                        statusCode = (int)response.StatusCode,
                        messageId = messageId,
                    });

                    return true;
                }
            }
            catch (WebException ex)
            {
                var statusCode = 0;
                string responseBody = null;
                var httpResponse = ex.Response as HttpWebResponse;
                if (httpResponse != null)
                {
                    try
                    {
                        statusCode = (int)httpResponse.StatusCode;
                        responseBody = ReadStreamSafe(httpResponse.GetResponseStream());
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { httpResponse.Close(); } catch { }
                    }
                }

                errorMessage = statusCode > 0
                    ? string.Format(CultureInfo.InvariantCulture, "Resend API returned HTTP {0}.", statusCode)
                    : "Resend API request failed: " + ex.Message;

                LogSafe(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "password-reset-email-failed",
                    provider = ProviderName,
                    statusCode = statusCode,
                    error = ex.Message,
                    providerResponse = Truncate(responseBody, 512),
                });

                return false;
            }
            catch (Exception ex)
            {
                errorMessage = "Resend API request failed: " + ex.Message;
                LogSafe(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "password-reset-email-failed",
                    provider = ProviderName,
                    error = ex.Message,
                });
                return false;
            }
        }

        private static string BuildPlainTextBody(string displayName, string resetCode, int ttlMinutes)
        {
            var greeting = !IsNullOrWhiteSpace(displayName)
                ? string.Format(CultureInfo.InvariantCulture, "Hello {0},", displayName.Trim())
                : "Hello,";

            var sb = new StringBuilder();
            sb.AppendLine(greeting);
            sb.AppendLine();
            sb.AppendLine("A password reset was requested for your BostonUnlocked account.");
            sb.AppendLine();
            sb.AppendLine("Enter this one-time reset code in the launcher:");
            sb.AppendLine();
            sb.AppendLine("    " + resetCode);
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "This code expires in {0} minutes and can only be used once.", ttlMinutes));
            sb.AppendLine();
            sb.AppendLine("If you did not request a password reset, you can safely ignore this email.");
            return sb.ToString();
        }

        private static string BuildHtmlBody(string displayName, string resetCode, int ttlMinutes)
        {
            var greeting = !IsNullOrWhiteSpace(displayName)
                ? "Hello " + HttpUtility.HtmlEncode(displayName.Trim()) + ","
                : "Hello,";
            var encodedCode = HttpUtility.HtmlEncode(resetCode);

            var sb = new StringBuilder();
            sb.Append("<div style=\"font-family:Segoe UI,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#1f2933;\">");
            sb.Append("<h2 style=\"margin:0 0 16px 0;font-size:20px;\">BostonUnlocked Password Reset</h2>");
            sb.Append("<p style=\"margin:0 0 12px 0;\">").Append(greeting).Append("</p>");
            sb.Append("<p style=\"margin:0 0 16px 0;\">A password reset was requested for your BostonUnlocked account. Enter the one-time reset code below in the game launcher:</p>");
            sb.Append("<div style=\"background:#f1f5f9;border:1px solid #cbd5e1;border-radius:6px;padding:16px;text-align:center;font-family:Consolas,Menlo,monospace;font-size:26px;font-weight:700;letter-spacing:3px;color:#0f172a;margin:16px 0;\">");
            sb.Append(encodedCode);
            sb.Append("</div>");
            sb.Append("<p style=\"margin:0 0 12px 0;color:#475569;font-size:14px;\">This code expires in ");
            sb.Append(ttlMinutes.ToString(CultureInfo.InvariantCulture));
            sb.Append(" minutes and can only be used once.</p>");
            sb.Append("<p style=\"margin:0;color:#64748b;font-size:13px;\">If you did not request a password reset, you can safely ignore this email.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string ReadStreamSafe(Stream stream)
        {
            if (stream == null)
            {
                return null;
            }

            try
            {
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string TryExtractId(string responseBody)
        {
            if (IsNullOrWhiteSpace(responseBody))
            {
                return null;
            }

            try
            {
                var dict = Json.DeserializeObject(responseBody) as Dictionary<string, object>;
                if (dict != null && dict.ContainsKey("id"))
                {
                    return dict["id"] as string;
                }
            }
            catch
            {
            }

            return null;
        }

        private static string Truncate(string value, int maxLen)
        {
            if (value == null || value.Length <= maxLen)
            {
                return value;
            }

            return value.Substring(0, maxLen) + "...";
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
