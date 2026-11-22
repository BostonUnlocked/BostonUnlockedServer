namespace Shadowrun.LocalService.Core.Email
{
    public interface IPasswordResetEmailSender
    {
        bool IsEnabled { get; }
        string ProviderName { get; }
        bool TrySendPasswordResetCode(string toEmail, string displayName, string resetCode, int ttlMinutes, out string errorMessage);
    }
}
