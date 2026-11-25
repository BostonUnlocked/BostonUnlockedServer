using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using SRO.Core.Compatibility.Math;

namespace Shadowrun.LocalService.Core.Simulation
{
    public sealed partial class ServerSimulationSession
    {
        private const int MaxDebugCommandTraceEntries = 64;

        private readonly object _debugTraceLock = new object();
        private readonly List<object> _debugCommandTrace = new List<object>();
        private int _debugCommandSeq;

        internal void RecordDebugCommandTrace(
            string cmd,
            int agentId,
            int? weaponIndex,
            int? skillIndex,
            int? skillId,
            int? targetX,
            int? targetY,
            bool meaningfulProgress,
            string errorMessage,
            object beforeState,
            object afterState)
        {
            lock (_debugTraceLock)
            {
                _debugCommandSeq++;
                var entry = new
                {
                    seq = _debugCommandSeq,
                    ts = RequestLogger.UtcNowIso(),
                    cmd = cmd ?? string.Empty,
                    agentId = agentId,
                    weaponIndex = weaponIndex,
                    skillIndex = skillIndex,
                    skillId = skillId,
                    targetX = targetX,
                    targetY = targetY,
                    meaningfulProgress = meaningfulProgress,
                    error = errorMessage,
                    before = beforeState,
                    after = afterState,
                };

                _debugCommandTrace.Add(entry);
                if (_debugCommandTrace.Count > MaxDebugCommandTraceEntries)
                {
                    _debugCommandTrace.RemoveAt(0);
                }
            }
        }

        internal object GetLastDebugCommandTrace()
        {
            lock (_debugTraceLock)
            {
                if (_debugCommandTrace.Count == 0)
                {
                    return null;
                }
                return _debugCommandTrace[_debugCommandTrace.Count - 1];
            }
        }

        internal object[] GetRecentDebugCommandTraces(int limit)
        {
            lock (_debugTraceLock)
            {
                if (_debugCommandTrace.Count == 0)
                {
                    return new object[0];
                }
                var count = limit > 0 && limit < _debugCommandTrace.Count ? limit : _debugCommandTrace.Count;
                var start = _debugCommandTrace.Count - count;
                var result = new object[count];
                for (var i = 0; i < count; i++)
                {
                    result[i] = _debugCommandTrace[start + i];
                }
                return result;
            }
        }

        internal object BuildDebugStateSnapshot(
            string sessionId,
            uint seed0,
            uint seed1,
            uint seed2,
            uint seed3,
            bool includeInactive,
            bool includeInteractives,
            bool includeStatusValues)
        {
            int commandSeq;
            lock (_debugTraceLock)
            {
                commandSeq = _debugCommandSeq;
            }

            var hasMissionEnded = false;
            var victoriousPlayers = new ulong[0];
            try
            {
                if (_gameworld != null && _gameworld.MissionOutcomeTrackingSystem != null)
                {
                    hasMissionEnded = _gameworld.MissionOutcomeTrackingSystem.HasMissionEnded;
                    if (hasMissionEnded)
                    {
                        var victors = _gameworld.MissionOutcomeTrackingSystem.GetVictoriousPlayers();
                        if (victors != null)
                        {
                            victoriousPlayers = victors.ToArray();
                        }
                    }
                }
            }
            catch
            {
            }

            var worldStatusValues = new Dictionary<string, float>(StringComparer.Ordinal);
            var currentRound = 0;
            try
            {
                if (_gameworld != null && _gameworld.EntitySystem != null)
                {
                    AttributeBackedStatusValueContainer envStatus;
                    if (_gameworld.EntitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(EnvironmentEntity.Instance, out envStatus) && envStatus != null)
                    {
                        foreach (var sv in envStatus)
                        {
                            if (sv != null)
                            {
                                worldStatusValues[sv.Id.ToString(CultureInfo.InvariantCulture)] = sv.Value;
                            }
                        }
                        if (envStatus.DoesContain(524291UL))
                        {
                            currentRound = (int)envStatus[524291UL].Value;
                        }
                    }
                }
            }
            catch
            {
            }

            var currentTeamId = _turnObserver != null && _turnObserver.CurrentTeam != null ? (int?)_turnObserver.CurrentTeam.ID : null;
            var currentTeamAi = _turnObserver != null && _turnObserver.CurrentTeam != null ? (bool?)_turnObserver.CurrentTeam.AIControlled : null;
            var activatableIds = _turnObserver != null && _turnObserver.CurrentActivatableMembers != null
                ? _turnObserver.CurrentActivatableMembers.Where(e => e != null).Select(e => e.Id).ToArray()
                : new int[0];

            var rngSeeds = new uint[4];
            try
            {
                if (_random != null)
                {
                    var cur = _random.CurrentSeeds;
                    rngSeeds[0] = cur.Seed0;
                    rngSeeds[1] = cur.Seed1;
                    rngSeeds[2] = cur.Seed2;
                    rngSeeds[3] = cur.Seed3;
                }
            }
            catch
            {
            }

            var actors = new List<object>();
            var interactives = new List<object>();

            if (_gameworld != null && _gameworld.EntitySystem != null)
            {
                var es = _gameworld.EntitySystem;
                var allEntities = es.GetAllEntities().Where(e => e != null).OrderBy(e => e.Id).ToArray();
                for (var i = 0; i < allEntities.Length; i++)
                {
                    var entity = allEntities[i];
                    if (entity.Id == EnvironmentEntity.Instance.Id)
                    {
                        continue;
                    }

                    GameplayPropertiesComponent gp;
                    es.TryGetComponent<GameplayPropertiesComponent>(entity, out gp);
                    if (gp == null)
                    {
                        continue;
                    }

                    TeamComponent team;
                    es.TryGetComponent<TeamComponent>(entity, out team);

                    CharacterStateComponent charState;
                    es.TryGetComponent<CharacterStateComponent>(entity, out charState);

                    CharacterSpawnInfoComponent spawnInfo;
                    es.TryGetComponent<CharacterSpawnInfoComponent>(entity, out spawnInfo);

                    if (gp.InteractiveObject)
                    {
                        if (!includeInteractives)
                        {
                            continue;
                        }

                        APositionComponent posComp;
                        es.TryGetComponent<APositionComponent>(entity, out posComp);
                        var blocked = new List<int[]>();
                        if (posComp != null && posComp.BlockedGridPositions != null)
                        {
                            try
                            {
                                foreach (var bp in posComp.BlockedGridPositions)
                                {
                                    blocked.Add(new int[] { bp.X, bp.Y });
                                }
                            }
                            catch
                            {
                            }
                        }

                        interactives.Add(new
                        {
                            entityId = entity.Id,
                            teamId = team != null ? (int?)team.TeamID : null,
                            uniqueId = gp.UniqueGameObjectIdentifier ?? string.Empty,
                            characterId = gp.CharacterId ?? string.Empty,
                            spawnManagerTag = spawnInfo != null ? (spawnInfo.SpawnManagerTag ?? string.Empty) : string.Empty,
                            x = posComp != null ? (int?)posComp.GridPosition.X : null,
                            y = posComp != null ? (int?)posComp.GridPosition.Y : null,
                            blockedCells = blocked.ToArray(),
                            isActive = charState != null ? (bool?)charState.Active : null,
                            isDespawned = charState != null ? (bool?)charState.Despawned : null,
                            isActivatable = gp.IsActivatable,
                            ignoredInTargetSelection = gp.IgnoredInTargetSelection,
                            requiredSkillId = gp.ActivityRequiredForInteraction,
                            spawnLootOnDeathActivity = gp.SpawnLootOnDeathActivity,
                        });
                        continue;
                    }

                    if (team == null)
                    {
                        continue;
                    }

                    var isDespawned = charState != null && charState.Despawned;
                    var isActive = charState == null || charState.Active;

                    AttributeBackedStatusValueContainer status;
                    es.TryGetComponent<AttributeBackedStatusValueContainer>(entity, out status);

                    var hp = status != null ? (int)status.GetByIdOrReturnDefault(458753UL) : 0;
                    var maxHp = hp;
                    if (status != null && status.DoesContain(458753UL))
                    {
                        try { maxHp = (int)status[458753UL].MaxValue; }
                        catch { maxHp = hp; }
                    }
                    var isDead = hp <= 0;

                    if (!includeInactive && (isDead || isDespawned || !isActive))
                    {
                        continue;
                    }

                    ControlComponent control;
                    es.TryGetComponent<ControlComponent>(entity, out control);

                    int? posX = null;
                    int? posY = null;
                    try
                    {
                        var pos = es.GetAgentGridPosition(entity);
                        posX = pos.X;
                        posY = pos.Y;
                    }
                    catch
                    {
                        IPositionComponent pComp;
                        if (es.TryGetComponent<IPositionComponent>(entity, out pComp) && pComp != null)
                        {
                            posX = pComp.GridPosition.X;
                            posY = pComp.GridPosition.Y;
                        }
                    }

                    int? walkRange = null;
                    int? sprintRange = null;
                    int? effectiveMoveRange = null;
                    if (status != null)
                    {
                        try { walkRange = es.GetWalkRange(entity); } catch { }
                        try { sprintRange = es.GetSprintRange(entity); } catch { }
                        try { effectiveMoveRange = es.GetEffectiveMovementRange(entity, null); } catch { }
                    }

                    var activeWeaponIndex = 0;
                    var weaponsList = new List<object>();
                    var interactionSkillIds = new List<ulong>();
                    ISkillLoadoutComponent loadout;
                    if (es.TryGetComponent<ISkillLoadoutComponent>(entity, out loadout) && loadout != null)
                    {
                        activeWeaponIndex = loadout.SelectedWeaponIndex;
                        if (loadout.Weapons != null)
                        {
                            for (var wIdx = 0; wIdx < loadout.Weapons.Length; wIdx++)
                            {
                                var w = loadout.Weapons[wIdx];
                                if (w == null)
                                {
                                    continue;
                                }
                                var skIds = new List<ulong>();
                                if (w.Skills != null)
                                {
                                    for (var sIdx = 0; sIdx < w.Skills.Length; sIdx++)
                                    {
                                        if (w.Skills[sIdx] != null)
                                        {
                                            skIds.Add(w.Skills[sIdx].Id);
                                        }
                                    }
                                }
                                weaponsList.Add(new
                                {
                                    index = wIdx,
                                    weaponId = w.Id,
                                    itemId = w.ItemId ?? string.Empty,
                                    name = w.Name ?? string.Empty,
                                    ammo = w.CurrentCharges,
                                    clipSize = w.MaximumCharges,
                                    autoRecharge = w.AutoRecharge,
                                    isConsumableBelt = w.IsConsumableBelt,
                                    skillIds = skIds.ToArray(),
                                });
                            }
                        }

                        if (loadout.InteractionSkills != null)
                        {
                            for (var k = 0; k < loadout.InteractionSkills.Count; k++)
                            {
                                if (loadout.InteractionSkills[k] != null)
                                {
                                    interactionSkillIds.Add(loadout.InteractionSkills[k].Id);
                                }
                            }
                        }
                    }

                    var cooldownsList = new List<object>();
                    CooldownComponent cdComp;
                    if (es.TryGetComponent<CooldownComponent>(entity, out cdComp) && cdComp != null)
                    {
                        foreach (var cdEntry in cdComp)
                        {
                            if (cdEntry.Value != null && cdEntry.Value.OnCooldown)
                            {
                                cooldownsList.Add(new
                                {
                                    skillId = cdEntry.Key,
                                    remainingRounds = cdEntry.Value.Current,
                                });
                            }
                        }
                    }

                    var statusEffectsList = new List<object>();
                    StatusEffectComponent seComp;
                    if (es.TryGetComponent<StatusEffectComponent>(entity, out seComp) && seComp != null && seComp.StatusEffects != null)
                    {
                        foreach (var se in seComp.StatusEffects)
                        {
                            if (se != null)
                            {
                                statusEffectsList.Add(new
                                {
                                    id = se.Id,
                                    category = se.StatusEffectCategory,
                                    duration = se.Duration,
                                });
                            }
                        }
                    }

                    Dictionary<string, float> rawStatusMap = null;
                    if (includeStatusValues && status != null)
                    {
                        rawStatusMap = new Dictionary<string, float>(StringComparer.Ordinal);
                        foreach (var sv in status)
                        {
                            if (sv != null)
                            {
                                rawStatusMap[sv.Id.ToString(CultureInfo.InvariantCulture)] = sv.Value;
                            }
                        }
                    }

                    var charName = gp.PlayerCharacterSnapshot != null && !string.IsNullOrEmpty(gp.PlayerCharacterSnapshot.CharacterName)
                        ? gp.PlayerCharacterSnapshot.CharacterName
                        : (gp.CharacterId ?? string.Empty);

                    actors.Add(new
                    {
                        entityId = entity.Id,
                        teamId = team.TeamID,
                        controlPlayerId = control != null ? control.PlayerId : 0UL,
                        isAiControlled = control != null ? control.IsAIControlled : team.IsAIControlled(),
                        isPlayersPlayerCharacter = gp.IsPlayersPlayerCharacter,
                        isHenchman = gp.PlayerCharacterSnapshot != null && gp.PlayerCharacterSnapshot.IsHenchman,
                        characterName = charName,
                        characterId = gp.CharacterId ?? string.Empty,
                        characterIdentifier = gp.PlayerCharacterSnapshot != null ? (gp.PlayerCharacterSnapshot.CharacterIdentifier ?? string.Empty) : string.Empty,
                        uniqueId = gp.UniqueGameObjectIdentifier ?? string.Empty,
                        spawnManagerTag = spawnInfo != null ? (spawnInfo.SpawnManagerTag ?? string.Empty) : string.Empty,
                        x = posX,
                        y = posY,
                        hp = hp,
                        maxHp = maxHp,
                        armor = status != null ? status.GetByIdOrReturnDefault(458763UL) : 0f,
                        armorMagic = status != null ? status.GetByIdOrReturnDefault(458764UL) : 0f,
                        armorTech = status != null ? status.GetByIdOrReturnDefault(458765UL) : 0f,
                        stdActions = status != null ? status.GetByIdOrReturnDefault(458754UL) : 0f,
                        moveActions = status != null ? status.GetByIdOrReturnDefault(458755UL) : 0f,
                        walkRange = walkRange,
                        sprintRange = sprintRange,
                        effectiveMoveRange = effectiveMoveRange,
                        isDead = isDead,
                        isActive = isActive,
                        isDespawned = isDespawned,
                        activeWeaponIndex = activeWeaponIndex,
                        weapons = weaponsList.ToArray(),
                        interactionSkillIds = interactionSkillIds.ToArray(),
                        cooldowns = cooldownsList.ToArray(),
                        statusEffects = statusEffectsList.ToArray(),
                        statusValues = rawStatusMap,
                    });
                }
            }

            var triggeredLootTables = _lootController != null
                ? _lootController.SnapshotTriggeredLootTables()
                : new string[0];

            return new
            {
                ok = true,
                sessionId = sessionId ?? string.Empty,
                mapName = _missionDefinition != null ? (_missionDefinition.Name ?? string.Empty) : string.Empty,
                level = _missionDefinition != null ? (_missionDefinition.Level ?? string.Empty) : string.Empty,
                commandSeq = commandSeq,
                isMissionStarted = IsMissionStarted,
                isMissionStopped = IsMissionStopped,
                hasMissionEnded = hasMissionEnded,
                victoriousPlayerIds = victoriousPlayers,
                round = currentRound,
                currentTeamId = currentTeamId,
                currentTeamAi = currentTeamAi,
                activatableEntityIds = activatableIds,
                seeds = new uint[] { seed0, seed1, seed2, seed3 },
                rngState = rngSeeds,
                actors = actors.ToArray(),
                interactives = interactives.ToArray(),
                worldStatusValues = worldStatusValues,
                triggeredLootTables = triggeredLootTables,
            };
        }
    }
}
