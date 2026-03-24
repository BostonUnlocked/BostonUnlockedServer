using System;
using System.Security.Cryptography;

namespace Shadowrun.LocalService.Core.Protocols
{
    public sealed partial class APlayTcpStub
    {
        private const uint LegacySeed0 = 0x11111111u;
        private const uint LegacySeed1 = 0x22222222u;
        private const uint LegacySeed2 = 0x33333333u;
        private const uint LegacySeed3 = 0x44444444u;

        private static readonly RNGCryptoServiceProvider MissionSeedRng = new RNGCryptoServiceProvider();

        private struct MissionSeedSet
        {
            public MissionSeedSet(uint seed0, uint seed1, uint seed2, uint seed3)
            {
                Seed0 = seed0;
                Seed1 = seed1;
                Seed2 = seed2;
                Seed3 = seed3;
            }

            public readonly uint Seed0;
            public readonly uint Seed1;
            public readonly uint Seed2;
            public readonly uint Seed3;
        }

        private MissionSeedSet AllocateMissionSeeds(string mode, string peer, string mapName, string coopGroupName)
        {
            var useFixedMissionSeeds = _options != null && _options.UseFixedMissionSeeds;
            var seeds = CreateMissionSeedSet(useFixedMissionSeeds);
            LogMissionSeedEvent("allocated", mode, peer, mapName, coopGroupName, seeds);
            return seeds;
        }

        private void LogMissionSeedEvent(string action, string mode, string peer, string mapName, string coopGroupName, MissionSeedSet seeds)
        {
            try
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "mission-seeds",
                    action = action,
                    mode = mode,
                    peer = peer,
                    mapName = mapName,
                    coopGroupName = coopGroupName,
                    seed0 = seeds.Seed0,
                    seed1 = seeds.Seed1,
                    seed2 = seeds.Seed2,
                    seed3 = seeds.Seed3,
                    useFixed = _options != null && _options.UseFixedMissionSeeds,
                });
            }
            catch
            {
            }
        }

        private static MissionSeedSet CreateMissionSeedSet(bool useFixedMissionSeeds)
        {
            if (useFixedMissionSeeds)
            {
                return new MissionSeedSet(LegacySeed0, LegacySeed1, LegacySeed2, LegacySeed3);
            }

            var seedBytes = new byte[16];
            MissionSeedRng.GetBytes(seedBytes);

            var seed0 = EnsureNonZeroSeed(BitConverter.ToUInt32(seedBytes, 0), 0x9E3779B9u);
            var seed1 = EnsureNonZeroSeed(BitConverter.ToUInt32(seedBytes, 4), 0x85EBCA6Bu);
            var seed2 = EnsureNonZeroSeed(BitConverter.ToUInt32(seedBytes, 8), 0xC2B2AE35u);
            var seed3 = EnsureNonZeroSeed(BitConverter.ToUInt32(seedBytes, 12), 0x27D4EB2Fu);

            return new MissionSeedSet(seed0, seed1, seed2, seed3);
        }

        private static uint EnsureNonZeroSeed(uint seed, uint fallback)
        {
            if (seed != 0u)
            {
                return seed;
            }

            var tickSeed = unchecked((uint)Environment.TickCount);
            var mixed = seed ^ tickSeed ^ fallback;
            return mixed != 0u ? mixed : fallback;
        }
    }
}
