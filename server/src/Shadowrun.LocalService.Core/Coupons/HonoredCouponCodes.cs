using System;
using System.Collections.Generic;
using System.Text;

namespace Shadowrun.LocalService.Core.Coupons
{
    internal static class HonoredCouponCodes
    {
        private static readonly Dictionary<string, bool> Allowed = CreateAllowedSet();

        public static bool IsHonored(string code)
        {
            var normalized = NormalizeCouponCode(code);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            bool ignored;
            return Allowed.TryGetValue(normalized, out ignored);
        }

        private static Dictionary<string, bool> CreateAllowedSet()
        {
            var set = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            AddCode(set, "SRO-Addon1");
            AddCode(set, "SRO-Addon2");
            AddCode(set, "SRO-GAME-KEY");
            AddCode(set, "SRO-ITEM-CATALYST");
            AddCode(set, "SRO-ITEM-CATALYST-SKUA");
            AddCode(set, "SRO-ITEM-CATALYST-SPRAYDOWN");
            AddCode(set, "SRO-ITEM-COMMUNITY-GOODIES1");
            AddCode(set, "SRO-ITEM-GAMESROCKET");
            AddCode(set, "SRO-ITEM-HAREBRAINED");
            AddCode(set, "SRO-ITEM-LAGISSUES1");
            AddCode(set, "SRO-ITEM-PAX-18");
            AddCode(set, "SRO-ITEM-PEGASUS");
            AddCode(set, "SRO-ITEM-STARTER-GEAR");
            AddCode(set, "UnobtainableCosmetics");
            AddCode(set, "SRO-KS-AWAKENED");
            AddCode(set, "SRO-KS-COOP");
            AddCode(set, "SRO-KS-MASTER");
            AddCode(set, "SRO-ReflexRecorder");
            AddCode(set, "SRO-STEAM-DLC-DELUXE");

            return set;
        }

        private static void AddCode(Dictionary<string, bool> set, string code)
        {
            if (set == null || IsNullOrWhiteSpace(code))
            {
                return;
            }

            var normalized = NormalizeCouponCode(code);
            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            set[normalized] = true;
        }

        private static string NormalizeCouponCode(string value)
        {
            if (IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                }
            }

            return sb.ToString();
        }

        private static bool IsNullOrWhiteSpace(string value)
        {
            if (value == null)
            {
                return true;
            }

            for (var i = 0; i < value.Length; i++)
            {
                if (!char.IsWhiteSpace(value[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}