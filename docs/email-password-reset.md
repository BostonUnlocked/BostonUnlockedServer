# Email Password Reset (Non-Steam / Cliffhanger Accounts)

BostonUnlockedServer supports the native game launcher password reset flow for non-Steam (`Cliffhanger`) accounts using [Resend](https://resend.com) as the transactional email provider.

## How the Flow Works

1. The player clicks **Reset Password** in the launcher and enters their account email address.
2. The launcher calls `POST /AccountSystem/.../Accounts/Cliffhanger/RequestPasswordReset`.
3. The server verifies that the email belongs to a non-Steam account, enforces rate limits, generates a single-use 8-character code (`XXXX-XXXX`), stores its SHA-256 hash in SQLite (`password_reset_codes`), and emails the code via Resend.
4. The launcher prompts for the one-time reset code and a new password, then calls `POST /AccountSystem/.../Accounts/Cliffhanger/ChangePassword`.
5. The server validates the hashed code (expiration + max attempts), updates the account's PBKDF2-SHA1 password hash with a fresh salt, marks the code used, and invalidates existing sessions for that account.

If email sending is not configured, `RequestPasswordReset` continues to return HTTP `501 NotSupported`.

---

## Setting Up Resend & Verifying a Domain

Resend's free tier includes **3,000 emails/month (100/day)** with no credit card required and does not expire.

### 1. Create a Resend Account
1. Go to [https://resend.com](https://resend.com) and sign up (email or GitHub).
2. No credit card is required for the free tier.

### 2. Add & Verify Your Sending Domain
To send password reset emails to arbitrary player email addresses (and avoid spam folders), you must verify a domain (or subdomain) that you control:

1. In the Resend dashboard, open **Domains** → **Add Domain**.
2. Enter your domain or a dedicated sending subdomain (recommended, e.g. `mail.yourdomain.com`).
3. Choose the region closest to your server.
4. Resend will display DNS records to add at your DNS provider (Cloudflare, Namecheap, Route 53, Porkbun, etc.):
   - **DKIM (`TXT`)** — cryptographic signature proving emails came from your domain.
   - **SPF (`TXT` + `MX` on `send.<subdomain>`)** — authorizes Resend's mail servers to send on your behalf.
   - **DMARC (`TXT` on `_dmarc.<yourdomain>`, optional but recommended)** — e.g.:
     ```
     v=DMARC1; p=none;
     ```
     (You can tighten to `p=quarantine` once verified.)
5. Add those records in your DNS control panel and click **Verify DNS Records** in Resend. Verification usually completes within a few minutes (up to 24 hours depending on DNS propagation).

### 3. Create an API Key
1. In the Resend dashboard, open **API Keys** → **Create API Key**.
2. Name it (e.g. `bostonunlocked-prod`).
3. Set permission to **Sending access** and restrict it to your verified domain.
4. Copy the generated key (`re_...`) — Resend only shows it once.

---

## Server Configuration

You can configure email password reset via a JSON file in your server `DataDir` (`email-config.json`, alongside `chat-admins.json` and `localservice.sqlite`) **or** via environment variables. Environment variables take precedence over `email-config.json`.

### Option A: `email-config.json` (in `<DataDir>\email-config.json`)

```json
{
  "provider": "resend",
  "resendApiKey": "re_your_api_key_here",
  "fromAddress": "BostonUnlocked <noreply@mail.yourdomain.com>",
  "subject": "BostonUnlocked Password Reset Code",
  "resetCodeTtlMinutes": 15,
  "maxVerifyAttempts": 5,
  "emailCooldownSeconds": 120,
  "maxRequestsPerEmailPerHour": 5,
  "maxRequestsPerIpPerHour": 15
}
```

> Do **not** commit `email-config.json` or your Resend API key to git. `server/data/` is already gitignored.

### Option B: Environment Variables

| Variable | Required | Default | Description |
|---|---|---|---|
| `SRO_RESEND_API_KEY` | Yes | *(none)* | Resend API key (`re_...`) |
| `SRO_EMAIL_FROM` | Yes | *(none)* | Verified sender address, e.g. `BostonUnlocked <noreply@mail.yourdomain.com>` |
| `SRO_EMAIL_PROVIDER` | No | `resend` | Email provider identifier |
| `SRO_EMAIL_RESET_SUBJECT` | No | `BostonUnlocked Password Reset Code` | Email subject line |
| `SRO_PASSWORD_RESET_CODE_TTL_MINUTES` | No | `15` | Minutes before a reset code expires (1–1440) |
| `SRO_PASSWORD_RESET_MAX_ATTEMPTS` | No | `5` | Max wrong guesses before the code is invalidated (1–20) |
| `SRO_PASSWORD_RESET_COOLDOWN_SECONDS` | No | `120` | Minimum seconds between reset requests for the same email |
| `SRO_PASSWORD_RESET_MAX_PER_EMAIL_HOUR` | No | `5` | Max reset emails per account per rolling hour |
| `SRO_PASSWORD_RESET_MAX_PER_IP_HOUR` | No | `15` | Max reset requests per client IP per rolling hour |
