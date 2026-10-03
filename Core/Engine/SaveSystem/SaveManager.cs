using Chisel.EXScript;
using Engine.Compilation;
using Engine.Console;
using Engine.Entities;
using Engine.Entities.LogicEntities;
using Engine.Physics;
using Engine.Rendering;
using Engine.Scripting.Sound;
using Engine.Utils;
using Engine.Utils.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rockwall;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Engine.SaveSystem
{
    public struct Save
    {
        public struct EntitySaveData
        {
            public int entityId;
            public Guid entitySaveID;
            public EntityReference reference;
            public Vector3 velocity;
            public CustomSaveData customData;
            public bool isBrushEntity;
            public bool isCurrentlySimulated;

            /// <summary>
            /// Legacy single-brush field from before brush entities could own more than one brush.
            /// Kept only so old save files still deserialize.
            /// </summary>
            public int brush;
            public int[] brushes;

            public Dictionary<string, EntityOutput[]> outputs;

            /// <summary>
            /// Migrates an old single-brush save entry (brush >= 0, brushes null/empty) into the
            /// current brushes[] shape. Safe to call on already-current entries (no-op).
            /// </summary>
            public void NormalizeLegacyBrush()
            {
                if ((brushes == null || brushes.Length == 0) && brush >= 0)
                    brushes = new[] { brush };
            }
        }
        public struct MapStateSaveData
        {
            public string mapPath;
            public EntitySaveData[] entityData;
            public Dictionary<string, PackedEXValue> mapGlobals;
        }

        public string name;
        public string map;
        public string activeSoundscape;
        public Vector3[] soundscapeAnchors;
        public EntitySaveData[] entityData;
        public CustomSaveData customData;
        public Dictionary<string, PackedEXValue> globalStates;
        public Dictionary<string, PackedEXValue> mapGlobals;
        public Dictionary<string, MapStateSaveData> mapStates;
    }
    public static class SaveManager
    {
        private static string savePath => $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create)}/{GameSettings.GameName}";
        private static Save currentSave;
        private static Dictionary<string, Save.MapStateSaveData> sessionMapStates = new();
        private static JsonSerializerSettings settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
        };
        public static Save GetActiveSave() => currentSave;

        private static void ApplyCommonSaveFields(WorldEntity ent, Save.EntitySaveData entityRef, Vector3 positionOffset)
        {
            ent.Position = entityRef.reference.Position + positionOffset;
            ent.Rotation = entityRef.reference.Rotation;
            ent.Velocity = entityRef.velocity;

            ent.EntityOutputs = entityRef.outputs;
            RehydrateOutputScripts(ent.EntityOutputs);

            ent.Scale = entityRef.reference.Scale;
            ent.properties = entityRef.reference.Properties;
            ent.Name = entityRef.reference.Name;
            ent.IsSimulated = entityRef.isCurrentlySimulated;
        }
        private static void RehydrateOutputScripts(Dictionary<string, EntityOutput[]>? outputs)
        {
            if (outputs == null) return;
            foreach (var key in outputs.Keys)
            {
                var arr = outputs[key];
                for (int i = 0; i < arr.Length; i++)
                {
                    var output = arr[i];
                    WorldEntity.TryParseOutputScript(key, ref output);
                    arr[i] = output;
                }
            }
        }
        private static void ApplyBrushEntityData(WorldEntity ent, Save.EntitySaveData entityRef, Vector3 positionOffset)
        {
            if (!entityRef.isBrushEntity) return;

            entityRef.NormalizeLegacyBrush();
            if (entityRef.brushes == null || entityRef.brushes.Length == 0) return;

            var brushEnt = (BrushEntity)ent;
            brushEnt.brushSet = entityRef.brushes;
            brushEnt.Position = GlobalMapData.ActiveMap.Brushes[entityRef.brushes[0]].Position + positionOffset;

            foreach (var bi in entityRef.brushes)
            {
                if (bi < 0 || bi >= GlobalMapData.ActiveMap.Brushes.Length) continue;
                GlobalMapData.ActiveMap.Brushes[bi].Entity = brushEnt;
            }
        }

        private static WorldEntity RestoreEntityFromSaveData(Save.EntitySaveData entityRef, Vector3 positionOffset = default)
        {
            if (entityRef.reference.EntityName == null) return null;
            if (!EntityCompiler.EntityLookupTable.ContainsKey(entityRef.reference.EntityName)) return null;

            var ent = (WorldEntity)Activator.CreateInstance(EntityCompiler.EntityLookupTable[entityRef.reference.EntityName]);
            ApplyCommonSaveFields(ent, entityRef, positionOffset);
            ApplyBrushEntityData(ent, entityRef, positionOffset);
            return ent;
        }

        /// <summary>
        /// Returns a copy of all cached map states for this session.
        /// </summary>
        public static IReadOnlyDictionary<string, Save.MapStateSaveData> SessionMapStates => sessionMapStates;

        /// <summary>
        /// Clears all cached map states for this session (e.g. starting a new game).
        /// </summary>
        public static void ClearSessionMapStates()
        {
            sessionMapStates.Clear();
        }

        private enum SaveAction
        {
            None,
            MakeSave,
            LoadSave,
            DeleteSave,
            LevelTransition
        }
        public enum SaveType
        {
            Manual,
            Quick,
            Auto
        }

        private static SaveType nextSaveType;
        private static SaveAction nextAction = 0;
        private static string saveName;

        private const int TotalAutosaves = 5;
        private static int autosaveIndex = 0;

        /// <summary>
        /// Captures the current map's entity/global state into the session cache.
        /// </summary>
        public static void CaptureCurrentMapState(List<WorldEntity> excludeEntities = null)
        {
            if (MainEngine.Instance.ActiveMapPath == null) return;

            string saveMapName = Path.GetFileNameWithoutExtension(MainEngine.Instance.ActiveMapPath);

            var stateData = new Save.MapStateSaveData
            {
                mapPath = saveMapName,
                mapGlobals = MapGlobals.Save(),
                entityData = BuildEntitySaveData(excludeEntities)
            };

            sessionMapStates[saveMapName] = stateData;
        }

        /// <summary>
        /// Restores entity/global state for the map that was just loaded, if a cached state exists.
        /// </summary>
        public static void RestoreMapStateIfCached(string mapName)
        {
            if (!sessionMapStates.TryGetValue(mapName, out var state)) return;

            MapGlobals.Load(state.mapGlobals);

            RestoreAndSpawnBatch(state.entityData);
        }
        private struct PendingTransition
        {
            public string mapPath;
            public List<WorldEntity> transitioningEntities;
            public Vector3 originLogicPosition; // position of LogicTransitionLevel in OLD map
            public Vector3 destinationLogicPosition; // position of LogicTransitionLevel in NEW map (resolved after load)
            public string destinationEntityName; // name of the matching LogicTransitionLevel in the target map
        }

        private static PendingTransition? pendingTransition = null;

        /// <summary>
        /// Queues a level transition. Entities inside the transition volume will be carried over,
        /// offset relative to the LogicTransitionLevel entity with the given name in the destination map.
        /// </summary>
        /// <param name="mapPath">Full path to the destination map.</param>
        /// <param name="transitioningEntities">Entities captured inside the transition volume.</param>
        /// <param name="originLogicPosition">World position of the LogicTransitionLevel in the current map.</param>
        /// <param name="destinationEntityName">Name of the LogicTransitionLevel in the destination map to anchor to.</param>
        public static void QueueLevelTransition(
            string mapPath,
            List<WorldEntity> transitioningEntities,
            Vector3 originLogicPosition,
            string destinationEntityName)
        {
            // Snapshot current map state before we leave it
            CaptureCurrentMapState(transitioningEntities);

            pendingTransition = new PendingTransition
            {
                mapPath = mapPath,
                transitioningEntities = new List<WorldEntity>(transitioningEntities),
                originLogicPosition = originLogicPosition,
                destinationEntityName = destinationEntityName
            };

            nextAction = SaveAction.LevelTransition;
        }

        /// <summary>
        /// Makes a new save game.
        /// </summary>
        /// <param name="name">The name for this save.</param>
        public static void SaveGame(SaveType saveType = SaveType.Manual, string saveToOverride = null)
        {
            saveName = saveType switch
            {
                SaveType.Auto => $"{autosaveIndex}_auto",
                SaveType.Quick => $"quick",
                _ => System.DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
            };

            if(saveType == SaveType.Auto)
            {
                autosaveIndex++;
                autosaveIndex %= TotalAutosaves;
            }

            if (saveToOverride != null) saveName = saveToOverride;

            nextAction = SaveAction.MakeSave;
            nextSaveType = saveType;
        }
        /// <summary>
        /// Load a save game.
        /// </summary>
        /// <param name="name">The save name to load from. Default/Null means to load the latest.</param>
        public static void LoadGame(string name = null)
        {
            saveName = name;
            nextAction = SaveAction.LoadSave;
        }

        /// <summary>
        /// Finds the latest manual, quick, or autosave that matches the desired session.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public static string GetLatestSave()
        {
            DateTime newest = DateTime.MinValue;
            string finalPath = null;
            var files = GetAllSaves();
            files = files.Where(f => f.ToLower().EndsWith(".sav"));

            foreach (var file in files)
            {
                var time = File.GetLastWriteTime(file);
                if(time > newest)
                {
                    newest = time;
                    finalPath = file;
                }
            }
            return finalPath;
        }
        /// <summary>
        /// Gets all saves in the game save directory.
        /// </summary>
        public static IEnumerable<string> GetAllSaves()
        {
            // thanks to @ratchet3789 and @bitl for randomly finding this
            if (!Path.Exists(savePath))
                Directory.CreateDirectory(savePath);

            var files = Directory.EnumerateFiles(savePath);
            return files;
        }

        /// <summary>
        /// Handles the next save action, eg. load, delete, create.
        /// </summary>
        public static void Handle()
        {
            switch (nextAction)
            {
                case SaveAction.LoadSave:
                    {
                        var path = saveName == null ? GetLatestSave() : $"{savePath}/{saveName}.sav";
                        if (string.IsNullOrEmpty(path)) break;

                        currentSave = JsonConvert.DeserializeObject<Save>(File.ReadAllText(path), settings);
                        currentSave.globalStates = GlobalState.Save();
                        sessionMapStates = currentSave.mapStates != null
                            ? new Dictionary<string, Save.MapStateSaveData>(currentSave.mapStates)
                            : new();

                        var entityData = currentSave.entityData;

                        if(!string.IsNullOrEmpty(currentSave.activeSoundscape) && currentSave.soundscapeAnchors != null)
                        {
                            SoundscapeManager.StartSoundscape(currentSave.activeSoundscape);
                            SoundscapeManager.Anchors = currentSave.soundscapeAnchors;
                        }

                        MainEngine.Instance.LoadMap(
                            Path.Combine(MainEngine.FullPath, currentSave.map),
                            disableEntitySpawning: true,
                            onComplete: () =>
                            {
                                RestoreAndSpawnBatch(entityData);

                                MainEngine.Instance.PostLoadFrameSleep = 10;

                                Logger.AppendInfo($"Loaded @ {PhysicsEngine.AllBodies.Count} bodies and {PhysicsEngine.AllShapes.Count} shapes");
                            });

                        break;
                    }
                case SaveAction.MakeSave:

                    if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);

                    CaptureCurrentMapState();

                    var spath = $"{savePath}/{saveName}.sav";

                    using (var screenshot = RenderEngine.TakeScreenshot(512, 512))
                    {
                        screenshot.SaveAsPng(File.OpenWrite($"{spath}.png"), 512, 512);
                    }

                    currentSave.entityData = BuildEntitySaveData();
                    GlobalState.Load(currentSave.globalStates);
                    currentSave.map = Path.GetRelativePath(MainEngine.FullPath, MainEngine.Instance.ActiveMapPath);
                    currentSave.mapStates = new Dictionary<string, Save.MapStateSaveData>(sessionMapStates);
                    currentSave.activeSoundscape = SoundscapeManager.CurrentSoundscape;
                    currentSave.soundscapeAnchors = SoundscapeManager.Anchors;

                    string json = JsonConvert.SerializeObject(currentSave, settings);
                    File.WriteAllText(spath, json);

                    Logger.AppendInfo($"Saved @ {PhysicsEngine.AllBodies.Count} bodies and {PhysicsEngine.AllShapes.Count} shapes");

                    break;
                case SaveAction.LevelTransition:
                    {
                        if (pendingTransition == null) break;
                        var transition = pendingTransition.Value;
                        pendingTransition = null;

                        string absolutePath = Path.Combine(
                            Path.GetDirectoryName(MainEngine.Instance.ActiveMapPath),
                            transition.mapPath);

                        bool hasCachedState = sessionMapStates.ContainsKey(
                            Path.GetFileNameWithoutExtension(transition.mapPath));

                        var snapshots = BuildEntitySaveData(
                            include: transition.transitioningEntities);

                        Vector3 originLogicPos = transition.originLogicPosition;
                        string destEntityName = transition.destinationEntityName;

                        MainEngine.Instance.LoadMap(absolutePath, disableEntitySpawning: hasCachedState,
                            onComplete: () =>
                            {
                                if (hasCachedState)
                                    RestoreMapStateIfCached(Path.GetFileNameWithoutExtension(transition.mapPath));

                                EntityManager.UpdateEntities(new GameTime());

                                Vector3 destLogicPos = Vector3.Zero;
                                if (destEntityName != null)
                                {
                                    var indices = EntityManager.FindEntityIndexByName(destEntityName);
                                    if (indices != null && indices.Length > 0)
                                    {
                                        var anchor = EntityManager.entities[indices[0]];
                                        if (anchor != null) destLogicPos = anchor.WorldPosition;
                                    }
                                }

                                Vector3 offset = destLogicPos - originLogicPos;

                                var restored = new List<(WorldEntity ent, Save.EntitySaveData snap, bool isNew)>();

                                foreach (var snap in snapshots)
                                {
                                    if (snap.reference.EntityName == null) continue;
                                    if (!EntityCompiler.EntityLookupTable.ContainsKey(snap.reference.EntityName)) continue;

                                    var entityIDinMap = EntityManager.entities.FindIndex(
                                        e => e != null && e.SaveID == snap.entitySaveID);

                                    var newEnt = entityIDinMap == -1
                                        ? (WorldEntity)Activator.CreateInstance(EntityCompiler.EntityLookupTable[snap.reference.EntityName])
                                        : EntityManager.entities[entityIDinMap];

                                    ApplyCommonSaveFields(newEnt, snap, offset);
                                    ApplyBrushEntityData(newEnt, snap, offset);

                                    restored.Add((newEnt, snap, entityIDinMap == -1));
                                }

                                LinkMoveParentsAndSpawn(restored.Select(r => (r.ent, r.snap)).ToList());

                                foreach (var (ent, snap, isNew) in restored)
                                {
                                    if (isNew)
                                        EntityManager.SpawnEntity(ent, snap.customData, snap.entitySaveID);
                                    else
                                        ent.RestoreCustomData(snap.customData);
                                }

                                EntityManager.UpdateEntities(new GameTime());
                                MainEngine.Instance.PostLoadFrameSleep = 10;
                            });

                        break;
                    }
            }
            nextAction = SaveAction.None;
        }
        private static Save.EntitySaveData[] BuildEntitySaveData(List<WorldEntity> exclude = null, List<WorldEntity> include = null)
        {
            var list = new List<Save.EntitySaveData>();
            int i = 0;
            var source = include != null
                         ? CollectionsMarshal.AsSpan(include)
                         : EntityManager.entities.GetValues();
            foreach (WorldEntity entity in source)
            {
                if (entity == null) { i++; continue; }
                if (exclude != null && exclude.Contains(entity)) { i++; continue; }
                if (entity is BrushEntity be)
                {
                    list.Add(new Save.EntitySaveData
                    {
                        entityId = i,
                        entitySaveID = be.SaveID,
                        reference = new EntityReference
                        {
                            EntityName = entity.GetType().Name,
                            Name = entity.Name,
                            Properties = entity.properties,
                            Position = entity.WorldPosition,
                            Rotation = entity.WorldRotation,
                            Scale = entity.WorldScale,
                            entityMoveParentName = entity.MoveParent?.Name
                        },
                        customData = entity.CaptureCustomData(),
                        isBrushEntity = true,
                        outputs = entity.EntityOutputs,
                        isCurrentlySimulated = entity.IsSimulated,
                        brush = -1,
                        brushes = be.brushSet
                    });
                }
                else
                {
                    list.Add(new Save.EntitySaveData
                    {
                        entityId = i,
                        entitySaveID = entity.SaveID,
                        reference = new EntityReference
                        {
                            EntityName = entity.GetType().Name,
                            Name = entity.Name,
                            Properties = entity.properties,
                            Position = entity.WorldPosition,
                            Rotation = entity.WorldRotation,
                            Scale = entity.WorldScale,
                            entityMoveParentName = entity.MoveParent?.Name
                        },
                        velocity = entity.Velocity,
                        outputs = entity.EntityOutputs,
                        isCurrentlySimulated = entity.IsSimulated,
                        customData = entity.CaptureCustomData()
                    });
                }

                i++;
            }
            return list.ToArray();
        }
        private static void LinkMoveParentsAndSpawn(List<(WorldEntity ent, Save.EntitySaveData data)> constructed)
        {
            var nameToEntity = new Dictionary<string, WorldEntity>();
            foreach (var (ent, _) in constructed)
            {
                if (!string.IsNullOrEmpty(ent.Name) && !nameToEntity.ContainsKey(ent.Name))
                    nameToEntity[ent.Name] = ent;
            }

            foreach (var (ent, data) in constructed)
            {
                var targetName = data.reference.entityMoveParentName;
                if (string.IsNullOrEmpty(targetName)) continue;

                if (nameToEntity.TryGetValue(targetName, out var parent))
                    ent.MoveParent = parent;
                else
                    Logger.AppendError($"MoveParent target '{targetName}' not found while restoring entity '{ent.Name}'");
            }

            EntityManager.ResolveMoveParentsAndConvertToLocal(constructed.Select(c => c.ent).ToList());
        }

        private static void RestoreAndSpawnBatch(IEnumerable<Save.EntitySaveData> entityData, Vector3 positionOffset = default)
        {
            var constructed = new List<(WorldEntity ent, Save.EntitySaveData data)>();

            foreach (var entityRef in entityData)
            {
                var ent = RestoreEntityFromSaveData(entityRef, positionOffset);
                if (ent == null) continue;

                constructed.Add((ent, entityRef));
            }

            LinkMoveParentsAndSpawn(constructed);

            foreach (var (ent, data) in constructed)
                EntityManager.SpawnEntity(ent, data.customData, data.entitySaveID);
        }
    }
}
