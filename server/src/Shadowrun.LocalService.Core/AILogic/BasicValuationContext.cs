using System;
using System.Collections.Generic;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.ArtificialIntelligence;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Map;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Skills;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.AILogic
{
    /// <summary>
    /// Minimal valuation context implementation.
    /// For now it exposes empty perceptions and provides conservative defaults.
    /// </summary>
    public sealed class BasicValuationContext : IValuationContext
    {
        private readonly IGameworldInstance _gameworld;
        private readonly IAgentSnapshot _protagonist;
        private readonly Dictionary<ChanceToHitCacheKey, float> _chanceToHitCache;

        private bool _hasActivityContext;
        private int _weaponIndex;
        private int _skillIndex;
        private ulong _activityId;

        public BasicValuationContext(IGameworldInstance gameworld, IAgentSnapshot protagonist)
        {
            _gameworld = gameworld;
            _protagonist = protagonist;
            _chanceToHitCache = new Dictionary<ChanceToHitCacheKey, float>();
            EnemySnapshots = new List<IAgentSnapshot>();
            PerceivedEnemies = new List<Entity>();
        }

        public BasicValuationContext(IGameworldInstance gameworld, IAgentSnapshot protagonist, int weaponIndex, int skillIndex, ulong activityId)
            : this(gameworld, protagonist)
        {
            SetActivityContext(weaponIndex, skillIndex, activityId);
        }

        public void SetActivityContext(int weaponIndex, int skillIndex, ulong activityId)
        {
            _weaponIndex = weaponIndex;
            _skillIndex = skillIndex;
            _activityId = activityId;
            _hasActivityContext = activityId != 0UL;
            _chanceToHitCache.Clear();
        }

        public IEnumerable<Entity> PerceivedEnemies { get; private set; }

        public ICollection<IAgentSnapshot> EnemySnapshots { get; private set; }

        public IAgentSnapshot ProtagonistSnapshot
        {
            get { return _protagonist; }
        }

        public IGameworldInstance Gameworld
        {
            get { return _gameworld; }
        }

        public float ChanceToHit(IntVector2D attackerPosition, int range, float accuracy, float toHitModifier, Entity target)
        {
            if (_gameworld == null || target == null)
            {
                return 0f;
            }

            var cacheKey = new ChanceToHitCacheKey(attackerPosition, range, accuracy, toHitModifier, target.Id, _weaponIndex, _skillIndex, _activityId, _hasActivityContext);
            float cached;
            if (_chanceToHitCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }

            float result;
            if (TryEvaluateChanceToHitFromDryRun(target, out result))
            {
                _chanceToHitCache[cacheKey] = result;
                return result;
            }

            result = EvaluateFallbackChanceToHit(attackerPosition, accuracy, toHitModifier, target);
            _chanceToHitCache[cacheKey] = result;
            return result;
        }

        public bool IsWithinWalkRange(IntVector2D position)
        {
            if (_gameworld == null || _gameworld.ReachableRangesCalculator == null || _protagonist == null || _protagonist.Entity == null)
            {
                return false;
            }

            try
            {
                ReachableRanges ranges = _gameworld.ReachableRangesCalculator.GetReachableRanges(_protagonist.Entity);
                if (ranges == null || ranges.WalkRange == null)
                {
                    return false;
                }

                return ranges.WalkRange.Contains(position);
            }
            catch
            {
                return false;
            }
        }

        public void Refresh()
        {
            _chanceToHitCache.Clear();
        }

        private bool TryEvaluateChanceToHitFromDryRun(Entity target, out float chanceToHit)
        {
            chanceToHit = 0f;

            if (!_hasActivityContext || _protagonist == null || _protagonist.Entity == null || _gameworld == null || _gameworld.ActivitySystem == null)
            {
                return false;
            }

            var dryRunner = _gameworld.ActivitySystem as IActivitySystemDryRunner;
            if (dryRunner == null)
            {
                return false;
            }

            IntVector2D targetPosition;
            try
            {
                targetPosition = _gameworld.EntitySystem.GetAgentGridPosition(target);
            }
            catch
            {
                return false;
            }

            ActivityEvaluationResult evaluation;
            try
            {
                evaluation = dryRunner.DryRunActivity(_weaponIndex, _skillIndex, _activityId, _protagonist.Entity, targetPosition);
            }
            catch
            {
                return false;
            }

            if (evaluation == null || evaluation.TargetWorkspaces == null || !evaluation.TargetWorkspaces.Contains(target))
            {
                return false;
            }

            chanceToHit = Clamp01(evaluation.TargetWorkspaces[target].ChanceToHit);
            return true;
        }

        private float EvaluateFallbackChanceToHit(IntVector2D attackerPosition, float accuracy, float toHitModifier, Entity target)
        {
            var baseChanceToHit = Clamp01(accuracy);

            ICover cover = null;
            try
            {
                if (_gameworld != null && _gameworld.CoverSystem != null)
                {
                    cover = _gameworld.CoverSystem.DetermineCover(attackerPosition, target);
                }
            }
            catch
            {
                cover = null;
            }

            var coverPenalty = cover != null ? cover.CTHPenalty : 0f;
            return Clamp01(baseChanceToHit - coverPenalty + toHitModifier);
        }

        private static float Clamp01(float value)
        {
            if (value < 0f)
            {
                return 0f;
            }

            if (value > 1f)
            {
                return 1f;
            }

            return value;
        }

        private struct ChanceToHitCacheKey : IEquatable<ChanceToHitCacheKey>
        {
            private readonly IntVector2D _attackerPosition;
            private readonly int _range;
            private readonly float _accuracy;
            private readonly float _toHitModifier;
            private readonly int _targetId;
            private readonly int _weaponIndex;
            private readonly int _skillIndex;
            private readonly ulong _activityId;
            private readonly bool _hasActivityContext;

            public ChanceToHitCacheKey(IntVector2D attackerPosition, int range, float accuracy, float toHitModifier, int targetId, int weaponIndex, int skillIndex, ulong activityId, bool hasActivityContext)
            {
                _attackerPosition = attackerPosition;
                _range = range;
                _accuracy = accuracy;
                _toHitModifier = toHitModifier;
                _targetId = targetId;
                _weaponIndex = weaponIndex;
                _skillIndex = skillIndex;
                _activityId = activityId;
                _hasActivityContext = hasActivityContext;
            }

            public bool Equals(ChanceToHitCacheKey other)
            {
                return _attackerPosition.Equals(other._attackerPosition)
                    && _range == other._range
                    && _accuracy.Equals(other._accuracy)
                    && _toHitModifier.Equals(other._toHitModifier)
                    && _targetId == other._targetId
                    && _weaponIndex == other._weaponIndex
                    && _skillIndex == other._skillIndex
                    && _activityId == other._activityId
                    && _hasActivityContext == other._hasActivityContext;
            }

            public override bool Equals(object obj)
            {
                return obj is ChanceToHitCacheKey && Equals((ChanceToHitCacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _attackerPosition.GetHashCode();
                    hash = (hash * 397) ^ _range;
                    hash = (hash * 397) ^ _accuracy.GetHashCode();
                    hash = (hash * 397) ^ _toHitModifier.GetHashCode();
                    hash = (hash * 397) ^ _targetId;
                    hash = (hash * 397) ^ _weaponIndex;
                    hash = (hash * 397) ^ _skillIndex;
                    hash = (hash * 397) ^ _activityId.GetHashCode();
                    hash = (hash * 397) ^ _hasActivityContext.GetHashCode();
                    return hash;
                }
            }
        }
    }
}
