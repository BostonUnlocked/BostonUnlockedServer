using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cliffhanger.Core.Compatibility.Loading;
using Cliffhanger.SRO.ServerClientCommons;
using Cliffhanger.SRO.ServerClientCommons.GameLogic;
using Cliffhanger.SRO.ServerClientCommons.GameLogic.Components;
using Cliffhanger.SRO.ServerClientCommons.Gameworld;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.CommandProcessing;
using GameSimulation = Cliffhanger.SRO.ServerClientCommons.Gameworld.CommandProcessing.Simulation;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Commands;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Communication;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Locomotion;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.RandomNumbers;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.StaticGameData;
using Cliffhanger.SRO.ServerClientCommons.Gameworld.Turnbased;
using Cliffhanger.SRO.ServerClientCommons.Matchmaking;
using Cliffhanger.SRO.ServerClientCommons.Metagameplay;
using Cliffhanger.SRO.ServerClientCommons.Trigger;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation;
using SRO.Core.Compatibility.Gameplay.Levelrepresentation.Serialization;
using SRO.Core.Compatibility.Logging;
using SRO.Core.Compatibility.Math;
using SRO.Core.Compatibility.Utilities;
using Shadowrun.LocalService.Core.AILogic;

namespace Shadowrun.LocalService.Core.Simulation
{
    public sealed partial class ServerSimulationSession
    {
        public const int EndTeamTurnSkillId = 99997;

        private readonly RequestLogger _logger;
        private readonly string _peer;

        private readonly IStaticData _staticData;
        private readonly MissionDefinition _missionDefinition;
        private readonly MatchConfiguration _matchConfiguration;
        private readonly ILevelData _levelData;

        private readonly XORShiftRandomNumberGenerator _random;
        private readonly GameworldInstance _gameworld;
        private readonly GameSimulation _simulation;
        private readonly GameworldController _controller;
        private readonly TurnObserver _turnObserver;

        private readonly bool _enableAiLogic;
        private readonly IAiDecisionEngine _aiDecisionEngine;

        private readonly LocalMissionLootController _lootController;
        private readonly EncounterActivationTracker _encounterActivationTracker;

        private readonly object _lootPreviewLock;
        private readonly List<string> _pendingLootPreviews;

        private ServerSimulationSession(
            RequestLogger logger,
            string peer,
            IStaticData staticData,
            MissionDefinition missionDefinition,
            MatchConfiguration matchConfiguration,
            ILevelData levelData,
            XORShiftRandomNumberGenerator random,
            GameworldInstance gameworld,
            GameSimulation simulation,
            GameworldController controller,
            TurnObserver turnObserver,
            LocalMissionLootController lootController,
            EncounterActivationTracker encounterActivationTracker,
            object lootPreviewLock,
            List<string> pendingLootPreviews,
            bool enableAiLogic,
            IAiDecisionEngine aiDecisionEngine)
        {
            _logger = logger;
            _peer = peer;
            _staticData = staticData;
            _missionDefinition = missionDefinition;
            _matchConfiguration = matchConfiguration;
            _levelData = levelData;
            _random = random;
            _gameworld = gameworld;
            _simulation = simulation;
            _controller = controller;
            _turnObserver = turnObserver;
            _lootController = lootController;
            _encounterActivationTracker = encounterActivationTracker ?? new EncounterActivationTracker();

            _lootPreviewLock = lootPreviewLock ?? new object();
            _pendingLootPreviews = pendingLootPreviews ?? new List<string>();

            _enableAiLogic = enableAiLogic;
            _aiDecisionEngine = aiDecisionEngine;
        }

        internal LocalMissionLootController.LootGrant[] DrainPendingLoot()
        {
            return _lootController != null ? _lootController.DrainPending() : new LocalMissionLootController.LootGrant[0];
        }

        internal string[] DrainPendingLootPreviews()
        {
            if (_pendingLootPreviews == null)
            {
                return new string[0];
            }

            lock (_lootPreviewLock)
            {
                if (_pendingLootPreviews.Count == 0)
                {
                    return new string[0];
                }

                var copy = _pendingLootPreviews.ToArray();
                _pendingLootPreviews.Clear();
                return copy;
            }
        }

        internal bool IsMissionStarted
        {
            get { return _simulation != null && _simulation.IsMissionStarted; }
        }

        internal bool IsMissionStopped
        {
            get { return _simulation != null && _simulation.IsMissionStopped; }
        }

        internal bool TryGetMissionOutcomeForPlayer(ulong playerId, out string outcome)
        {
            outcome = null;
            try
            {
                if (_gameworld == null || _gameworld.MissionOutcomeTrackingSystem == null)
                {
                    return false;
                }

                var tracker = _gameworld.MissionOutcomeTrackingSystem;
                if (!tracker.HasMissionEnded)
                {
                    return false;
                }

                var victors = tracker.GetVictoriousPlayers();
                if (victors == null)
                {
                    return false;
                }

                // If the player is not listed as victorious, treat it as a defeat/failure.
                var didWin = victors.Contains(playerId);
                outcome = didWin ? "Victory" : "Defeat";
                return true;
            }
            catch
            {
                outcome = null;
                return false;
            }
        }

        public static ServerSimulationSession Create(
            RequestLogger logger,
            string peer,
            string staticDataDir,
            string streamingAssetsDir,
            string mapName,
            string compressedMatchConfiguration,
            uint seed0,
            uint seed1,
            uint seed2,
            uint seed3,
            string storyLine,
            int chapter,
            bool enableAiLogic)
        {
            if (logger == null) throw new ArgumentNullException("logger");
            if (staticDataDir == null) throw new ArgumentNullException("staticDataDir");
            if (streamingAssetsDir == null) throw new ArgumentNullException("streamingAssetsDir");
            if (mapName == null) throw new ArgumentNullException("mapName");
            if (compressedMatchConfiguration == null) throw new ArgumentNullException("compressedMatchConfiguration");

            var jsonAdapter = new JsonCompositeFileAdapter(staticDataDir, "json");
            var staticData = StaticDataInstance.Load(StaticDataLoader.CreateForClient(jsonAdapter)).StaticData;

            var missionDefinition = ResolveMissionDefinition(staticData, mapName);

            var levelData = LoadLevelData(streamingAssetsDir, missionDefinition);

            var matchConfiguration = MissionSerializer.DeserializeMatchConfiguration(compressedMatchConfiguration, staticData.MetagameplayData);

            var random = new XORShiftRandomNumberGenerator(seed0, seed1, seed2, seed3);

            var missionId = new MissionID(1L);

            var settings = new GameworldInstanceFactorySettings();
            var factory = new GameworldInstanceFactory(staticData, settings);

            var gameworldInstance = factory.CreateGameworldInstance(levelData, random, missionDefinition, missionId) as GameworldInstance;

            // Retail uses a server-side MissionLootController; the client build ships with a no-op DefaultMissionLootController.
            // Install our own controller so GiveLootCalculationResult actually produces rewards.
            // We store grants in-memory and apply them to persistence on mission exit.
            var lootPreviewLock = new object();
            var pendingLootPreviews = new List<string>();
            var lootController = new LocalMissionLootController(
                staticData,
                random,
                storyLine,
                chapter,
                delegate(LocalMissionLootController.LootGrant grant)
                {
                    if (grant == null)
                    {
                        return;
                    }

                    if (!string.IsNullOrEmpty(grant.ItemId))
                    {
                        lock (lootPreviewLock)
                        {
                            pendingLootPreviews.Add(grant.ItemId);
                        }
                    }

                    try
                    {
                        logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "mission-loot-grant",
                            peer = peer,
                            lootTable = grant.LootTable,
                            itemId = grant.ItemId,
                            delta = grant.Delta,
                            sellPrice = grant.SellPrice,
                            nuyen = grant.Nuyen,
                        });
                    }
                    catch
                    {
                    }
                },
                delegate(string lootTable)
                {
                    try
                    {
                        logger.Log(new
                        {
                            ts = RequestLogger.UtcNowIso(),
                            type = "mission-loot-call",
                            peer = peer,
                            lootTable = lootTable,
                        });
                    }
                    catch
                    {
                    }
                });
            gameworldInstance.MissionLootController = lootController;

            // Environment entity + core components
            RegisterEnvironmentEntity(staticData, matchConfiguration, gameworldInstance.EntitySystem, gameworldInstance.Factions, levelData);

            // Critical: feed player/henchman snapshots into the mission template resolver before any spawns occur.
            // If we skip this, player spawns fall back to default templates and diverge from client stats (e.g., metatype movement bonuses).
            PopulateMissionEntityTemplateResolver(matchConfiguration, levelData, gameworldInstance);

            // Controllers and simulation
            gameworldInstance.TickTriggerController = new TickTriggerController(levelData);
            gameworldInstance.LieutenantSpawner = new LieutenantSpawner(staticData.AgentData);

            var simulation = CreateSimulation(missionId, gameworldInstance, levelData, random);

            var playerIds = GetAllPlayerIds(matchConfiguration);
            var controller = new GameworldController(gameworldInstance, simulation, playerIds);

            simulation.Initialize(controller);

            gameworldInstance.MetagameplayrelevantChangeObserver = NullMetagameplayrelevantChangeObserver.Instance;
            NullMetagameplayrelevantChangeObserver.Instance.Initialize(gameworldInstance.EntitySystem);

            gameworldInstance.Simulation = simulation;
            gameworldInstance.ActivePlayers = controller;

            var summonAttributeModificationFactory = new SummonAttributeModificationFactory(gameworldInstance.EntitySystem);
            var summoningSpawnServiceModule = new SummoningSpawnServiceModule(gameworldInstance.EntitySystem, summonAttributeModificationFactory);
            gameworldInstance.CharacterSpawnService = new CharacterSpawnService(gameworldInstance, summoningSpawnServiceModule);
            gameworldInstance.EntityStateChangeObserver.AddListener(gameworldInstance.CharacterSpawnService);

            gameworldInstance.PointOfInterestController = new PointOfInterestController(gameworldInstance.TriggerSystem, levelData, gameworldInstance.EntitySystem);

            var turnObserver = new TurnObserver();
            simulation.AddTurnPhaseListener(turnObserver);

            var encounterActivationTracker = new EncounterActivationTracker(logger, peer, gameworldInstance.EntitySystem);
            if (gameworldInstance.InMissionEventObserver != null)
            {
                gameworldInstance.InMissionEventObserver.Add(encounterActivationTracker);
            }

            controller.StartMission();
            controller.StartFirstRound();

            try
            {
                logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = peer,
                    status = "roster",
                    mapName = mapName,
                    missionLevel = missionDefinition.Level,
                    staticDataVersion = staticData.Globals.VersioningInfo != null ? (int?)staticData.Globals.VersioningInfo.Version : null,
                    match = BuildMatchRosterSummary(matchConfiguration),
                    entities = BuildEntityRosterSummary(gameworldInstance),
                });
            }
            catch (Exception ex)
            {
                logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = peer,
                    status = "roster-failed",
                    message = ex.Message,
                });
            }

            logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "sim",
                peer = peer,
                status = "turn-initial",
                missionStarted = simulation.IsMissionStarted,
                missionStopped = simulation.IsMissionStopped,
                teamId = turnObserver.CurrentTeam != null ? (int?)turnObserver.CurrentTeam.ID : null,
                teamAi = turnObserver.CurrentTeam != null ? (bool?)turnObserver.CurrentTeam.AIControlled : null,
                activatable = turnObserver.CurrentActivatableMembers != null ? turnObserver.CurrentActivatableMembers.Length : 0,
                activatableIds = turnObserver.CurrentActivatableMembers != null
                    ? turnObserver.CurrentActivatableMembers.Select(e => e != null ? e.Id : -1).ToArray()
                    : new int[0],
            });

            logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "sim",
                peer = peer,
                status = "started",
                mapName = mapName,
                level = missionDefinition.Level,
                staticDataDir = staticDataDir,
            });

            IAiDecisionEngine aiDecisionEngine;
            if (enableAiLogic)
            {
                aiDecisionEngine = new ConfigDrivenAiDecisionEngine(
                    new EntityComponentAiBehaviourConfigLookup(),
                    new SkillSelectionStrategyFactory(),
                    random);
            }
            else
            {
                aiDecisionEngine = new NullAiDecisionEngine();
            }

            return new ServerSimulationSession(
                logger,
                peer,
                staticData,
                missionDefinition,
                matchConfiguration,
                levelData,
                random,
                gameworldInstance,
                simulation,
                controller,
                turnObserver,
                lootController,
                encounterActivationTracker,
                lootPreviewLock,
                pendingLootPreviews,
                enableAiLogic,
                aiDecisionEngine);
        }

        private static void PopulateMissionEntityTemplateResolver(MatchConfiguration matchConfiguration, ILevelData levelData, GameworldInstance gameworldInstance)
        {
            if (matchConfiguration == null || matchConfiguration.Teams == null)
            {
                return;
            }

            if (levelData == null || levelData.TeamDefinitions == null)
            {
                return;
            }

            if (gameworldInstance == null || gameworldInstance.MissionEntityTemplateResolver == null)
            {
                return;
            }

            var resolver = gameworldInstance.MissionEntityTemplateResolver;
            var teamDefinitions = levelData.TeamDefinitions.ToArray();

            for (var teamIndex = 0; teamIndex < matchConfiguration.Teams.Count && teamIndex < teamDefinitions.Length; teamIndex++)
            {
                var matchTeam = matchConfiguration.Teams[teamIndex];
                var teamDefinition = teamDefinitions[teamIndex];
                if (matchTeam == null || teamDefinition == null)
                {
                    continue;
                }

                var teamId = teamDefinition.ID;
                var players = matchTeam.Players;
                if (players == null)
                {
                    continue;
                }

                foreach (var player in players)
                {
                    var human = player as HumanPlayer;
                    if (human == null)
                    {
                        continue;
                    }

                    if (human.PlayerCharacterSnapshot != null)
                    {
                        // The snapshot coming from the stub can have PlayerId=0; the sim expects it to match the owning player.
                        human.PlayerCharacterSnapshot.PlayerId = human.ID;
                        human.PlayerCharacterSnapshot.IsHenchman = false;
                        resolver.AddPlayerCharacter(teamId, human.PlayerCharacterSnapshot);
                    }

                    if (human.Henchmen != null && human.Henchmen.Length > 0)
                    {
                        foreach (var hench in human.Henchmen)
                        {
                            if (hench == null)
                            {
                                continue;
                            }

                            hench.PlayerId = human.ID;
                            hench.IsHenchman = true;
                        }

                        resolver.AddHenchmanCharacters(teamId, human.Henchmen);
                    }
                }
            }
        }

        private static object BuildMatchRosterSummary(MatchConfiguration matchConfiguration)
        {
            if (matchConfiguration == null)
            {
                return null;
            }

            var teams = matchConfiguration.Teams != null
                ? matchConfiguration.Teams.Select(t => new
                {
                    teamId = t != null ? (int?)t.ID : null,
                    players = t != null
                        ? t.Players.Select(p => new
                        {
                            playerId = p != null ? (ulong?)p.ID : null,
                            kind = p is HumanPlayer ? "human" : "ai",
                            snapshot = (p != null && p.PlayerCharacterSnapshot != null)
                                ? new
                                {
                                    dataVersion = p.PlayerCharacterSnapshot.DataVersion,
                                    snapshotPlayerId = p.PlayerCharacterSnapshot.PlayerId,
                                    characterName = p.PlayerCharacterSnapshot.CharacterName,
                                    identifier = p.PlayerCharacterSnapshot.CharacterIdentifier,
                                    identifierGuid = SafeGetIdentifierGuid(p.PlayerCharacterSnapshot),
                                    identifierExtension = SafeGetIdentifierExtension(p.PlayerCharacterSnapshot),
                                    bodytype = p.PlayerCharacterSnapshot.Bodytype,
                                    skinTextureIndex = p.PlayerCharacterSnapshot.SkinTextureIndex,
                                    backgroundStory = p.PlayerCharacterSnapshot.BackgroundStory,
                                    voiceset = p.PlayerCharacterSnapshot.Voiceset,
                                    skillTreeCount = p.PlayerCharacterSnapshot.SkillTreeDefinitions != null ? (int?)p.PlayerCharacterSnapshot.SkillTreeDefinitions.Count : null,
                                    wallet = p.PlayerCharacterSnapshot.Wallet != null ? "present" : "null",
                                    inventory = p.PlayerCharacterSnapshot.PlayerCharacterInventory != null ? "present" : "null",
                                }
                                : null,
                            henchmen = (p != null && p.Henchmen != null)
                                ? p.Henchmen.Select(h => new
                                {
                                    dataVersion = h != null ? (int?)h.DataVersion : null,
                                    identifier = h != null ? h.CharacterIdentifier : null,
                                    identifierExtension = h != null ? SafeGetIdentifierExtension(h) : null,
                                    bodytype = h != null ? (ulong?)h.Bodytype : null,
                                }).ToArray()
                                : new object[0],
                        }).ToArray()
                        : new object[0],
                }).ToArray()
                : new object[0];

            return new
            {
                mapName = matchConfiguration.Mapname,
                teams = teams,
            };
        }

        private static object BuildEntityRosterSummary(GameworldInstance gameworldInstance)
        {
            if (gameworldInstance == null || gameworldInstance.EntitySystem == null)
            {
                return null;
            }

            var entitySystem = gameworldInstance.EntitySystem;

            // Limit to character-like entities: must be on a team and not be an interactive object.
            var entities = entitySystem.GetAllEntities()
                .Where(e => entitySystem.HasComponent<TeamComponent>(e))
                .Where(e => entitySystem.HasComponent<GameplayPropertiesComponent>(e))
                .Select(e =>
                {
                    GameplayPropertiesComponent gp;
                    entitySystem.TryGetComponent<GameplayPropertiesComponent>(e, out gp);

                    if (gp != null && gp.InteractiveObject)
                    {
                        return null;
                    }

                    TeamComponent team;
                    entitySystem.TryGetComponent<TeamComponent>(e, out team);

                    ControlComponent control;
                    entitySystem.TryGetComponent<ControlComponent>(e, out control);

                    IActionCostEvaluator actionCostEvaluator;
                    var hasActionCostEvaluator = entitySystem.TryGetComponent<IActionCostEvaluator>(e, out actionCostEvaluator);

                    AttributeBackedStatusValueContainer status;
                    entitySystem.TryGetComponent<AttributeBackedStatusValueContainer>(e, out status);

                    int? posX = null;
                    int? posY = null;
                    try
                    {
                        var pos = entitySystem.GetAgentGridPosition(e);
                        posX = pos.X;
                        posY = pos.Y;
                    }
                    catch
                    {
                    }

                    return new
                    {
                        entityId = e.Id,
                        teamId = team != null ? (int?)team.TeamID : null,
                        teamAi = team != null ? (bool?)team.IsAIControlled() : null,
                        controlPlayerId = control != null ? (ulong?)control.PlayerId : null,
                        controlAi = control != null ? (bool?)control.IsAIControlled : null,
                        actionCostEvaluator = hasActionCostEvaluator && actionCostEvaluator != null ? actionCostEvaluator.GetType().FullName : null,
                        isPlayersPlayerCharacter = gp != null ? (bool?)gp.IsPlayersPlayerCharacter : null,
                        metaTypeId = gp != null ? (ulong?)gp.MetaTypeIdentifier : null,
                        animationMetaTypeId = gp != null ? (ulong?)gp.AnimationMetaTypeIdentifier : null,
                        snapshot = gp != null && gp.PlayerCharacterSnapshot != null
                            ? new
                            {
                                dataVersion = gp.PlayerCharacterSnapshot.DataVersion,
                                snapshotPlayerId = gp.PlayerCharacterSnapshot.PlayerId,
                                characterName = gp.PlayerCharacterSnapshot.CharacterName,
                                identifier = gp.PlayerCharacterSnapshot.CharacterIdentifier,
                                identifierGuid = SafeGetIdentifierGuid(gp.PlayerCharacterSnapshot),
                                identifierExtension = SafeGetIdentifierExtension(gp.PlayerCharacterSnapshot),
                                bodytype = gp.PlayerCharacterSnapshot.Bodytype,
                            }
                            : null,
                        posX = posX,
                        posY = posY,
                        walkRange = status != null ? (int?)entitySystem.GetWalkRange(e) : null,
                        sprintRange = status != null ? (int?)entitySystem.GetSprintRange(e) : null,
                        effectiveMoveRange = status != null ? (int?)entitySystem.GetEffectiveMovementRange(e, null) : null,
                        stdActions = status != null ? (float?)status.GetByIdOrReturnDefault(458754UL) : null,
                        moveActions = status != null ? (float?)status.GetByIdOrReturnDefault(458755UL) : null,
                    };
                })
                .Where(x => x != null)
                .ToArray();

            return new
            {
                count = entities.Length,
                entities = entities,
            };
        }

        private static string SafeGetIdentifierGuid(IPlayerCharacterSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            try
            {
                return StringGuidUtility.GetFirstPartOfIdentifier(snapshot);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeGetIdentifierExtension(IPlayerCharacterSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }

            try
            {
                return StringGuidUtility.GetCustomExtension(snapshot.CharacterIdentifier, ":");
            }
            catch
            {
                return null;
            }
        }

        public SeedPackage CreateSeedPackage()
        {
            return _random.CreateSeedPackage();
        }

        public void Stop()
        {
            _controller.Stop();
        }

        private bool ExecuteCommand(
            ICommand command,
            string commandName,
            int? weaponIndex,
            int? skillIndex,
            int? skillId,
            int? targetX,
            int? targetY)
        {
            if (command == null)
            {
                return false;
            }

            float beforeStd = 0f;
            float beforeMove = 0f;
            float afterStd = 0f;
            float afterMove = 0f;

            int? beforePosX = null;
            int? beforePosY = null;
            int? beforeWalkRange = null;
            int? beforeSprintRange = null;
            int? beforeEffectiveMoveRange = null;

            int? afterPosX = null;
            int? afterPosY = null;
            int? afterWalkRange = null;
            int? afterSprintRange = null;
            int? afterEffectiveMoveRange = null;

            try
            {
                Entity entity;
                if (_gameworld != null && _gameworld.EntitySystem != null && _gameworld.EntitySystem.TryGet(command.AgentId, out entity))
                {
                    var status = _gameworld.EntitySystem.GetComponent<AttributeBackedStatusValueContainer>(entity);
                    beforeStd = status.GetByIdOrReturnDefault(458754UL);
                    beforeMove = status.GetByIdOrReturnDefault(458755UL);

                    try
                    {
                        var pos = _gameworld.EntitySystem.GetAgentGridPosition(entity);
                        beforePosX = pos.X;
                        beforePosY = pos.Y;
                    }
                    catch
                    {
                    }

                    try
                    {
                        beforeWalkRange = _gameworld.EntitySystem.GetWalkRange(entity);
                        beforeSprintRange = _gameworld.EntitySystem.GetSprintRange(entity);
                        beforeEffectiveMoveRange = _gameworld.EntitySystem.GetEffectiveMovementRange(entity, null);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            var beforeTeam = _turnObserver.CurrentTeam;
            var beforeTeamId = beforeTeam != null ? (int?)beforeTeam.ID : null;
            var beforeTeamAi = beforeTeam != null ? (bool?)beforeTeam.AIControlled : null;
            var beforeActivatable = _turnObserver.CurrentActivatableMembers != null ? _turnObserver.CurrentActivatableMembers.Length : 0;

            try
            {
                // Execute directly so exceptions aren't swallowed by GameworldController.
                _simulation.Execute(command);
            }
            catch (Exception ex)
            {
                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim",
                    peer = _peer,
                    status = "execute-failed",
                    cmd = commandName,
                    agentId = command.AgentId,
                    message = ex.Message,
                });
                return false;
            }

            var afterTeam = _turnObserver.CurrentTeam;
            var afterTeamId = afterTeam != null ? (int?)afterTeam.ID : null;
            var afterTeamAi = afterTeam != null ? (bool?)afterTeam.AIControlled : null;
            var afterActivatable = _turnObserver.CurrentActivatableMembers != null ? _turnObserver.CurrentActivatableMembers.Length : 0;

            try
            {
                Entity entity;
                if (_gameworld != null && _gameworld.EntitySystem != null && _gameworld.EntitySystem.TryGet(command.AgentId, out entity))
                {
                    var status = _gameworld.EntitySystem.GetComponent<AttributeBackedStatusValueContainer>(entity);
                    afterStd = status.GetByIdOrReturnDefault(458754UL);
                    afterMove = status.GetByIdOrReturnDefault(458755UL);

                    try
                    {
                        var pos = _gameworld.EntitySystem.GetAgentGridPosition(entity);
                        afterPosX = pos.X;
                        afterPosY = pos.Y;
                    }
                    catch
                    {
                    }

                    try
                    {
                        afterWalkRange = _gameworld.EntitySystem.GetWalkRange(entity);
                        afterSprintRange = _gameworld.EntitySystem.GetSprintRange(entity);
                        afterEffectiveMoveRange = _gameworld.EntitySystem.GetEffectiveMovementRange(entity, null);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                // Best-effort diagnostics only.
            }

            _logger.Log(new
            {
                ts = RequestLogger.UtcNowIso(),
                type = "sim",
                peer = _peer,
                status = "executed",
                cmd = commandName,
                agentId = command.AgentId,
                weaponIndex = weaponIndex,
                skillIndex = skillIndex,
                skillId = skillId,
                targetX = targetX,
                targetY = targetY,
                beforeTeamId = beforeTeamId,
                beforeTeamAi = beforeTeamAi,
                beforeActivatable = beforeActivatable,
                beforeStdActions = beforeStd,
                beforeMoveActions = beforeMove,
                beforePosX = beforePosX,
                beforePosY = beforePosY,
                beforeWalkRange = beforeWalkRange,
                beforeSprintRange = beforeSprintRange,
                beforeEffectiveMoveRange = beforeEffectiveMoveRange,
                afterTeamId = afterTeamId,
                afterTeamAi = afterTeamAi,
                afterActivatable = afterActivatable,
                afterStdActions = afterStd,
                afterMoveActions = afterMove,
                afterPosX = afterPosX,
                afterPosY = afterPosY,
                afterWalkRange = afterWalkRange,
                afterSprintRange = afterSprintRange,
                afterEffectiveMoveRange = afterEffectiveMoveRange,
            });

            // Heuristic: did this command actually advance state? Used to prevent AI loops from spamming
            // when an action is repeatedly rejected by conditions.
            if (beforeTeamId != afterTeamId)
            {
                return true;
            }
            if (beforeTeamAi != afterTeamAi)
            {
                return true;
            }
            if (beforeActivatable != afterActivatable)
            {
                return true;
            }
            if (beforePosX != afterPosX || beforePosY != afterPosY)
            {
                return true;
            }
            if (Math.Abs(beforeStd - afterStd) > 0.001f)
            {
                return true;
            }
            if (Math.Abs(beforeMove - afterMove) > 0.001f)
            {
                return true;
            }

            return false;
        }

        public void ExecuteFollowPath(int agentId, int targetX, int targetY)
        {
            var cmd = new FollowPathCommand(agentId, new IntVector2D(targetX, targetY), _gameworld);
            ExecuteCommand(cmd, "FollowPath", null, null, null, targetX, targetY);
        }

        public void ExecuteActivateSkill(int weaponIndex, int skillIndex, int skillId, int agentId, int targetX, int targetY, SeedPackage seeds)
        {
            IntVector2D targetPos;

            // Client EndTurn() is encoded as ActivateActiveSkill(skillId=99997, x=0, y=0).
            // The shared command type is position-targeted, but the end-turn activity is effectively positionless.
            // Using (0,0) breaks the authoritative sim's turn advancement, which in turn prevents AI turns from being auto-ended.
            if (skillId == EndTeamTurnSkillId)
            {
                Entity entity;
                if (_gameworld.EntitySystem.TryGet(agentId, out entity))
                {
                    try
                    {
                        targetPos = _gameworld.EntitySystem.GetAgentGridPosition(entity);
                    }
                    catch
                    {
                        targetPos = new IntVector2D(targetX, targetY);
                    }
                }
                else
                {
                    targetPos = new IntVector2D(targetX, targetY);
                }
            }
            else
            {
                targetPos = new IntVector2D(targetX, targetY);
            }

            if (skillId == 90006)
            {
                TryLogInteractionTarget(agentId, targetPos);
            }

            var cmd = new ActivatePositionTargetedActiveSkillCommand(
                weaponIndex,
                skillIndex,
                skillId,
                agentId,
                targetPos,
                _gameworld,
                _random,
                seeds);

            ExecuteCommand(cmd, "ActivateActiveSkill", weaponIndex, skillIndex, skillId, targetPos.X, targetPos.Y);
        }

        private void TryLogInteractionTarget(int agentId, IntVector2D targetPos)
        {
            try
            {
                if (_gameworld == null || _gameworld.EntitySystem == null || _gameworld.RuntimeGrid == null)
                {
                    return;
                }

                var es = _gameworld.EntitySystem;
                var dynamicEntity = _gameworld.RuntimeGrid.GetEntityFromPosition(targetPos);
                var staticEntity = _gameworld.RuntimeGrid.GetStaticEntityFromPosition(targetPos);

                _logger.Log(new
                {
                    ts = RequestLogger.UtcNowIso(),
                    type = "sim-interaction-target",
                    peer = _peer,
                    agentId = agentId,
                    targetX = targetPos.X,
                    targetY = targetPos.Y,
                    dynamicEntity = BuildInteractionEntityDebug(es, dynamicEntity),
                    staticEntity = BuildInteractionEntityDebug(es, staticEntity),
                });
            }
            catch
            {
            }
        }

        private static object BuildInteractionEntityDebug(EntitySystem entitySystem, Entity entity)
        {
            if (entitySystem == null || entity == null)
            {
                return null;
            }

            GameplayPropertiesComponent gp;
            entitySystem.TryGetComponent<GameplayPropertiesComponent>(entity, out gp);

            APositionComponent pos;
            entitySystem.TryGetComponent<APositionComponent>(entity, out pos);

            object[] blocked = new object[0];
            if (pos != null)
            {
                try
                {
                    blocked = pos.BlockedGridPositions != null
                        ? pos.BlockedGridPositions.Select(p => (object)new { x = p.X, y = p.Y }).ToArray()
                        : new object[0];
                }
                catch
                {
                    blocked = new object[0];
                }
            }

            return new
            {
                id = entity.Id,
                hasGameplayProps = gp != null,
                interactiveObject = gp != null ? (bool?)gp.InteractiveObject : null,
                isActivatable = gp != null ? (bool?)gp.IsActivatable : null,
                ignoredInTargetSelection = gp != null ? (bool?)gp.IgnoredInTargetSelection : null,
                uniqueGameObjectIdentifier = gp != null ? gp.UniqueGameObjectIdentifier : null,
                characterId = gp != null ? gp.CharacterId : null,
                activityRequiredForInteraction = gp != null ? (ulong?)gp.ActivityRequiredForInteraction : null,
                spawnLootOnDeathActivity = gp != null ? (ulong?)gp.SpawnLootOnDeathActivity : null,
                specialHint = gp != null ? gp.SpecialHint : null,
                gridPosition = pos != null ? new { x = pos.GridPosition.X, y = pos.GridPosition.Y } : null,
                blockedCells = blocked,
            };
        }

        private IntVector2D TryGetAgentGridPositionOrDefault(Entity agent)
        {
            try
            {
                return _gameworld.EntitySystem.GetAgentGridPosition(agent);
            }
            catch
            {
                return new IntVector2D(0, 0);
            }
        }

        private static MissionDefinition ResolveMissionDefinition(IStaticData staticData, string mapName)
        {
            var mission = staticData.Globals.MissionData.MissionDefinitions.FirstOrDefault(m => m != null && string.Equals(m.Name, mapName, StringComparison.Ordinal));
            if (mission == null)
            {
                Logger.Default.Error("Could not resolve MissionDefinition for map '{0}'", new object[] { mapName });
                mission = staticData.Globals.MissionData.MissionDefinitions.FirstOrDefault(m => m != null) ?? new MissionDefinition();
            }
            return mission;
        }

        private static ILevelData LoadLevelData(string streamingAssetsDir, MissionDefinition missionDefinition)
        {
            var levelName = missionDefinition.Level;
            var mapDataPath = Path.Combine(Path.Combine(Path.Combine(streamingAssetsDir, "levels"), levelName), "mapdata.json");
            var serializer = new SceneObjectDataFileSerializer<Level>(JsonFxSerializerProvider.Current, new FileSystem());
            var level = serializer.LoadFromFile(mapDataPath);
            return LevelData.CreateFromSerializableLevel(levelName, level);
        }

        private static IEnumerable<ulong> GetAllPlayerIds(MatchConfiguration matchConfiguration)
        {
            var ids = new HashSet<ulong>();
            foreach (var player in matchConfiguration.Teams.SelectMany(t => t.Players))
            {
                ids.Add(player.ID);
            }
            return ids.ToArray();
        }

        private static void RegisterEnvironmentEntity(IStaticData staticData, MatchConfiguration matchConfiguration, EntitySystem entitySystem, PlayingFactions factions, ILevelData levelData)
        {
            var environmentData = staticData.Globals.EnvironmentData.Copy() as EnvironmentData;
            entitySystem.Register(EnvironmentEntity.Instance, environmentData.GameworldTemplate);

            entitySystem.AddComponent<PlayerInfoComponent>(EnvironmentEntity.Instance, new PlayerInfoComponent(matchConfiguration));
            entitySystem.AddComponent<TriggerScriptDefinition[]>(EnvironmentEntity.Instance, levelData.TriggerScriptDefinitions);

            var teamInfo = TeamInfoComponent.CreateFrom(matchConfiguration, levelData.TeamDefinitions);
            teamInfo.AddLootTeam();
            entitySystem.AddComponent<TeamInfoComponent>(EnvironmentEntity.Instance, teamInfo);

            entitySystem.AddComponent<TriggerPositionComponent>(EnvironmentEntity.Instance, new TriggerPositionComponent());

            var distinctHumanPlayers = (from team in matchConfiguration.Teams
                from player in team.Players
                where player is HumanPlayer
                select player.ID).Distinct().Count();

            Logger.Debug.Info("Setting number of distinct Human players to {0}", new object[] { distinctHumanPlayers });

            var status = entitySystem.GetComponent<AttributeBackedStatusValueContainer>(EnvironmentEntity.Instance);
            status[524288UL].SetUnmodifiedValue((float)distinctHumanPlayers);

            SetupFactionStatusValues(staticData, factions, status);
        }

        private static void SetupFactionStatusValues(IStaticData staticData, PlayingFactions factions, AttributeBackedStatusValueContainer gameworldStatusValueContainer)
        {
            foreach (string faction in factions.Factions)
            {
                var factionDef = staticData.Globals.FactionData.ResolveFaction(faction);
                if (factionDef != null)
                {
                    if (factionDef.GameworldStatusValue != 0)
                    {
                        var id = (ulong)((long)factionDef.GameworldStatusValue);
                        if (!gameworldStatusValueContainer.DoesContain(id))
                        {
                            gameworldStatusValueContainer.Add(new StatusValue(id, 1f));
                        }
                        else
                        {
                            gameworldStatusValueContainer[id].SetUnmodifiedValue(1f);
                        }
                    }
                }
                else
                {
                    Logger.Default.Error("Couldn't resolve faction {0} in definition!", new object[] { faction });
                }
            }
        }

        private static GameSimulation CreateSimulation(MissionID missionId, GameworldInstance gameworldInstance, ILevelData levelData, IRandomNumberGenerator random)
        {
            var simulation = new GameSimulation(missionId, gameworldInstance);

            simulation.AddSimulationPhaseListener(new AddMovementTriggerUpdateSimulationPhaseListener());
            simulation.AddSimulationPhaseListener(new AddSpawnManagerTriggerSimulationPhaseListener(levelData, random));
            simulation.AddSimulationPhaseListener(new AddRoundcountingTriggerSimulationPhaseListener());
            simulation.AddSimulationPhaseListener(new DecreasePlayerDeathCountdownListener());
            simulation.AddSimulationPhaseListener(new AddCheckIfMissionEndedTriggerSimulationPhaseListener(levelData));
            simulation.AddSimulationPhaseListener(new AddTriggerScriptsSimulationPhaseListener(levelData));
            simulation.AddSimulationPhaseListener(gameworldInstance.TickTriggerController);
            simulation.AddSimulationPhaseListener(gameworldInstance.TileTriggerController);

            simulation.AddTurnPhaseListener(gameworldInstance.ActionSystem);
            simulation.AddTurnPhaseListener(gameworldInstance.CooldownSystem);
            simulation.AddTurnPhaseListener(gameworldInstance.DetectionSystem);
            simulation.AddTurnPhaseListener(gameworldInstance.GameworldPresentationSystem);
            simulation.AddTurnPhaseListener(gameworldInstance.WeaponSystem);
            simulation.AddTurnPhaseListener(gameworldInstance.DerivedStatsSystem);

            return simulation;
        }

        public enum AiTurnActionKind
        {
            FollowPath = 1,
            ActivateActiveSkill = 2,
        }

        public sealed class AiTurnAction
        {
            public AiTurnAction(
                AiTurnActionKind kind,
                int agentId,
                int targetX,
                int targetY,
                int weaponIndex,
                int skillIndex,
                int skillId,
                SeedPackage seeds)
            {
                Kind = kind;
                AgentId = agentId;
                TargetX = targetX;
                TargetY = targetY;
                WeaponIndex = weaponIndex;
                SkillIndex = skillIndex;
                SkillId = skillId;
                Seeds = seeds;
            }

            public AiTurnActionKind Kind { get; private set; }
            public int AgentId { get; private set; }
            public int TargetX { get; private set; }
            public int TargetY { get; private set; }

            // ActivateActiveSkill only.
            public int WeaponIndex { get; private set; }
            public int SkillIndex { get; private set; }
            public int SkillId { get; private set; }
            public SeedPackage Seeds { get; private set; }
        }
    }
}
