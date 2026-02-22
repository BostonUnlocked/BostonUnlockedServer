using System;

namespace Shadowrun.LocalService.Core.Persistence
{
    public interface ISessionIdentityMap
    {
        void SetIdentityForSession(string sessionHash, string identityHash);
        bool TryGetIdentityForSession(string sessionHash, out string identityHash);
    }
}
