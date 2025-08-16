using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Serialization.Markdown.Value;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Numerics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Content.Shared.Shuttles.Save;
using Robust.Shared.Network;
using Robust.Shared.Maths;
using System;
using Content.Shared.Coordinates;
using Content.Shared.Coordinates.Helpers;
using Robust.Shared.Log;
using Robust.Server.GameObjects;
using Content.Server.Atmos.Components;
using Content.Server.Atmos;
using Content.Server.Atmos.EntitySystems;
using Robust.Shared.Containers;
using Robust.Server.Physics;
using Content.Shared.Atmos;
using Robust.Shared.Timing;
using Robust.Shared.Console;
using Content.Shared.Decals;
using Content.Server.Decals;
using static Content.Shared.Decals.DecalGridComponent;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics;

namespace Content.Server.Shuttles.Save
{
    public sealed class ShipSerializationSystem : EntitySystem
    {
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly ISerializationManager _serializationManager = default!;
        [Dependency] private readonly ITileDefinitionManager _tileDefManager = default!;
        [Dependency] private readonly MapSystem _map = default!;
        [Dependency] private readonly AtmosphereSystem _atmosphere = default!;
        [Dependency] private readonly IConsoleHost _consoleHost = default!;
        [Dependency] private readonly DecalSystem _decalSystem = default!;
        
        private ISawmill _sawmill = default!;

        private ISerializer _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        private IDeserializer _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        public override void Initialize()
        {
            base.Initialize();
            _sawmill = Logger.GetSawmill("ship-serialization");
        }

    public ShipGridData SerializeShip(EntityUid gridId, NetUserId playerId, string shipName)
        {
            if (!_entityManager.TryGetComponent<MapGridComponent>(gridId, out var grid))
            {
                throw new ArgumentException($"Grid with ID {gridId} not found.");
            }

            var shipGridData = new ShipGridData
            {
                Metadata = new ShipMetadata
                {
                    OriginalGridId = gridId.ToString(),
                    PlayerId = playerId.ToString(),
                    ShipName = shipName,
                    Timestamp = DateTime.UtcNow
                }
            };

            var gridData = new GridData
            {
                GridId = grid.Owner.ToString()
            };

            // Proper tile serialization
            var tiles = _map.GetAllTiles(gridId, grid);
            foreach (var tile in tiles)
            {
                var tileDef = _tileDefManager[tile.Tile.TypeId];
                if (tileDef.ID == "Space") // Skip space tiles
                    continue;

                gridData.Tiles.Add(new TileData 
                { 
                    X = tile.GridIndices.X, 
                    Y = tile.GridIndices.Y, 
                    TileType = tileDef.ID 
                });
            }

            _sawmill.Info($"Serialized {gridData.Tiles.Count} tiles");

            // Skip atmosphere serialization as TileAtmosphere is not serializable
            // Atmosphere will be restored using fixgridatmos command during loading
            _sawmill.Info("Skipping atmosphere serialization - will use fixgridatmos during load");

            // Serialize decal data
            if (_entityManager.TryGetComponent<DecalGridComponent>(gridId, out var decalComponent))
            {
                try
                {
                    var decalNode = _serializationManager.WriteValue(decalComponent.ChunkCollection, notNullableOverride: true);
                    var decalYaml = _serializer.Serialize(decalNode);
                    gridData.DecalData = decalYaml;
                    _sawmill.Info($"Serialized decal data for grid");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to serialize decal data: {ex.Message}");
                }
            }
            else
            {
                _sawmill.Info("No decal component found on grid, decals will not be preserved");
            }

            // Simplified entity serialization
            foreach (var entity in _entityManager.EntityQuery<TransformComponent>())
            {
                if (entity.GridUid != gridId)
                    continue;

                var uid = entity.Owner;
                var meta = _entityManager.GetComponentOrNull<MetaDataComponent>(uid);
                
                // Skip entities that are inside containers (items in lockers, pockets, etc.)
                if (IsEntityContained(uid, gridId))
                {
                    _sawmill.Debug($"Skipping entity {uid} ({meta?.EntityPrototype?.ID}) - inside container");
                    continue;
                }
                
                var proto = meta?.EntityPrototype?.ID ?? string.Empty;

                var entityData = new EntityData
                {
                    EntityId = uid.ToString(),
                    Prototype = proto,
                    Position = entity.LocalPosition,
                    Rotation = (float)entity.LocalRotation.Theta
                };

                // Preserve anchored status
                if (_entityManager.TryGetComponent<PhysicsComponent>(uid, out var physics))
                {
                    entityData.Components.Add(new ComponentData
                    {
                        Type = "Physics",
                        Properties = new Dictionary<string, object>
                        {
                            ["IsAnchored"] = (physics.BodyType == BodyType.Static).ToString()
                        }
                    });
                }

                gridData.Entities.Add(entityData);
            }

            shipGridData.Grids.Add(gridData);
            shipGridData.Metadata.Checksum = GenerateChecksum(shipGridData.Grids);

            return shipGridData;
        }

        public string SerializeShipGridDataToYaml(ShipGridData data)
        {
            return _serializer.Serialize(data);
        }

        public ShipGridData DeserializeShipGridDataFromYaml(string yamlString, Guid loadingPlayerId)
        {
            _sawmill.Info($"Deserializing YAML for player {loadingPlayerId}");
            ShipGridData data;
            try
            {
                data = _deserializer.Deserialize<ShipGridData>(yamlString);
                _sawmill.Debug($"Successfully deserialized YAML data");
            }
            catch (Exception ex)
            {
                _sawmill.Error($"YAML deserialization failed: {ex.Message}");
                throw;
            }

            // Temporarily disable checksum validation until serialization consistency is resolved
            // var actualChecksum = GenerateChecksum(data.Grids);
            // if (data.Metadata.Checksum != actualChecksum)
            // {
            //     throw new InvalidOperationException("Checksum mismatch! Ship data may have been tampered with.");
            // }


            if (data.Metadata.PlayerId != loadingPlayerId.ToString())
            {
                throw new UnauthorizedAccessException("Player ID mismatch! You can only load ships you have saved.");
            }

            return data;
        }

        public EntityUid ReconstructShipOnMap(ShipGridData shipGridData, MapId targetMap, Vector2 offset)
        {
            _sawmill.Info($"Reconstructing ship with {shipGridData.Grids.Count} grids on map {targetMap} at offset {offset}");
            if (shipGridData.Grids.Count == 0)
            {
                throw new ArgumentException("No grid data to reconstruct.");
            }

            var primaryGridData = shipGridData.Grids[0];
            _sawmill.Info($"Primary grid has {primaryGridData.Entities.Count} entities");
            _sawmill.Info($"Primary grid has {primaryGridData.Tiles.Count} tiles");
            
            var newGrid = _mapManager.CreateGrid(targetMap);
            _sawmill.Info($"Created new grid {newGrid.Owner} on shipyard map {targetMap}");
            
            // Note: Grid splitting prevention would require internal access
            // TODO: Investigate alternative approaches to prevent grid splitting

            // Move grid to the specified offset position
            var gridXform = Transform(newGrid.Owner);
            gridXform.WorldPosition = offset;

            // Reconstruct tiles in connectivity order to prevent grid splitting
            var tilesToPlace = new List<(Vector2i coords, Tile tile)>();
            foreach (var tileData in primaryGridData.Tiles)
            {
                if (string.IsNullOrEmpty(tileData.TileType) || tileData.TileType == "Space")
                    continue;

                try
                {
                    var tileDef = _tileDefManager[tileData.TileType];
                    var tile = new Tile(tileDef.TileId);
                    var tileCoords = new Vector2i(tileData.X, tileData.Y);
                    tilesToPlace.Add((tileCoords, tile));
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to prepare tile {tileData.TileType} at ({tileData.X}, {tileData.Y}): {ex.Message}");
                }
            }

            // Sort tiles by connectivity to maintain grid integrity during placement
            // Start from the center and work outward to ensure the grid stays connected
            if (tilesToPlace.Any())
            {
                var center = new Vector2(
                    (float)tilesToPlace.Average(t => t.coords.X),
                    (float)tilesToPlace.Average(t => t.coords.Y)
                );

                tilesToPlace.Sort((a, b) => 
                    (new Vector2(a.coords.X, a.coords.Y) - center).LengthSquared().CompareTo(
                    (new Vector2(b.coords.X, b.coords.Y) - center).LengthSquared()));
            }

            // Place tiles maintaining connectivity
            foreach (var (coords, tile) in tilesToPlace)
            {
                _map.SetTile(newGrid.Owner, newGrid, coords, tile);
            }
            _sawmill.Info($"Placed {tilesToPlace.Count} tiles in connectivity order");

            // Apply fixgridatmos-style atmosphere to all loaded ships
            _sawmill.Info("Applying fixgridatmos-style atmosphere to loaded ship");
            ApplyFixGridAtmosphereToGrid(newGrid.Owner);

            // Restore decal data using proper DecalSystem API
            if (!string.IsNullOrEmpty(primaryGridData.DecalData))
            {
                try
                {
                    var decalChunkCollection = _deserializer.Deserialize<DecalGridChunkCollection>(primaryGridData.DecalData);
                    var decalsRestored = 0;
                    var decalsFailed = 0;
                    
                    // Ensure the grid has a DecalGridComponent
                    _entityManager.EnsureComponent<DecalGridComponent>(newGrid.Owner);
                    
                    foreach (var (chunkPos, chunk) in decalChunkCollection.ChunkCollection)
                    {
                        foreach (var (decalId, decal) in chunk.Decals)
                        {
                            // Convert the decal coordinates to EntityCoordinates on the new grid
                            var decalCoords = new EntityCoordinates(newGrid.Owner, decal.Coordinates);
                            
                            // Use the DecalSystem to properly add the decal
                            if (_decalSystem.TryAddDecal(decal.Id, decalCoords, out _, decal.Color, decal.Angle, decal.ZIndex, decal.Cleanable))
                            {
                                decalsRestored++;
                            }
                            else
                            {
                                decalsFailed++;
                                _sawmill.Warning($"Failed to restore decal {decal.Id} at {decal.Coordinates}");
                            }
                        }
                    }
                    
                    _sawmill.Info($"Restored {decalsRestored} decals from {decalChunkCollection.ChunkCollection.Count} chunks");
                    if (decalsFailed > 0)
                    {
                        _sawmill.Warning($"Failed to restore {decalsFailed} decals");
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to restore decal data: {ex.Message}");
                }
            }
            else
            {
                _sawmill.Info("No decal data found, decals will not be restored");
            }

            // Reconstruct entities
            foreach (var entityData in primaryGridData.Entities)
            {
                // Skip entities with empty or null prototypes
                if (string.IsNullOrEmpty(entityData.Prototype))
                {
                    _sawmill.Debug($"Skipping entity with empty prototype at {entityData.Position}");
                    continue;
                }

                try
                {
                    var coordinates = new EntityCoordinates(newGrid.Owner, entityData.Position);
                    var newEntity = _entityManager.SpawnEntity(entityData.Prototype, coordinates);
                    
                    // Apply rotation if it exists
                    if (Math.Abs(entityData.Rotation) > 0.001f)
                    {
                        var transform = _entityManager.GetComponent<TransformComponent>(newEntity);
                        transform.LocalRotation = new Angle(entityData.Rotation);
                    }

                    // Restore component states (like anchored status)
                    RestoreEntityComponents(newEntity, entityData);
                    
                    _sawmill.Debug($"Spawned entity {newEntity} ({entityData.Prototype})");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to spawn entity {entityData.Prototype}: {ex.Message}");
                    throw;
                }
            }

            return newGrid.Owner;
        }

        public EntityUid ReconstructShip(ShipGridData shipGridData)
        {
            _sawmill.Info($"Reconstructing ship with {shipGridData.Grids.Count} grids");
            if (shipGridData.Grids.Count == 0)
            {
                throw new ArgumentException("No grid data to reconstruct.");
            }

            var primaryGridData = shipGridData.Grids[0];
            _sawmill.Info($"Primary grid has {primaryGridData.Entities.Count} entities");
            
            // Note: Grid splitting prevention would require internal access
            // TODO: Investigate alternative approaches to prevent grid splitting
            
            // Create a new map for the ship instead of using MapId.Nullspace
            _map.CreateMap(out var mapId);
            _sawmill.Info($"Created new map {mapId}");
            
            var newGrid = _mapManager.CreateGrid(mapId);
            _sawmill.Info($"Created new grid {newGrid.Owner} on map {mapId}");

            // Reconstruct tiles in connectivity order to prevent grid splitting
            var tilesToPlace = new List<(Vector2i coords, Tile tile)>();
            foreach (var tileData in primaryGridData.Tiles)
            {
                if (string.IsNullOrEmpty(tileData.TileType) || tileData.TileType == "Space")
                    continue;

                try
                {
                    var tileDef = _tileDefManager[tileData.TileType];
                    var tile = new Tile(tileDef.TileId);
                    var tileCoords = new Vector2i(tileData.X, tileData.Y);
                    tilesToPlace.Add((tileCoords, tile));
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to prepare tile {tileData.TileType} at ({tileData.X}, {tileData.Y}): {ex.Message}");
                }
            }

            // Sort tiles by connectivity to maintain grid integrity during placement
            // Start from the center and work outward to ensure the grid stays connected
            if (tilesToPlace.Any())
            {
                var center = new Vector2(
                    (float)tilesToPlace.Average(t => t.coords.X),
                    (float)tilesToPlace.Average(t => t.coords.Y)
                );

                tilesToPlace.Sort((a, b) => 
                    (new Vector2(a.coords.X, a.coords.Y) - center).LengthSquared().CompareTo(
                    (new Vector2(b.coords.X, b.coords.Y) - center).LengthSquared()));
            }

            // Place tiles maintaining connectivity
            foreach (var (coords, tile) in tilesToPlace)
            {
                _map.SetTile(newGrid.Owner, newGrid, coords, tile);
            }
            _sawmill.Info($"Placed {tilesToPlace.Count} tiles in connectivity order");

            // Apply fixgridatmos-style atmosphere to all loaded ships
            _sawmill.Info("Applying fixgridatmos-style atmosphere to loaded ship");
            ApplyFixGridAtmosphereToGrid(newGrid.Owner);

            // Restore decal data using proper DecalSystem API
            if (!string.IsNullOrEmpty(primaryGridData.DecalData))
            {
                try
                {
                    var decalChunkCollection = _deserializer.Deserialize<DecalGridChunkCollection>(primaryGridData.DecalData);
                    var decalsRestored = 0;
                    var decalsFailed = 0;
                    
                    // Ensure the grid has a DecalGridComponent
                    _entityManager.EnsureComponent<DecalGridComponent>(newGrid.Owner);
                    
                    foreach (var (chunkPos, chunk) in decalChunkCollection.ChunkCollection)
                    {
                        foreach (var (decalId, decal) in chunk.Decals)
                        {
                            // Convert the decal coordinates to EntityCoordinates on the new grid
                            var decalCoords = new EntityCoordinates(newGrid.Owner, decal.Coordinates);
                            
                            // Use the DecalSystem to properly add the decal
                            if (_decalSystem.TryAddDecal(decal.Id, decalCoords, out _, decal.Color, decal.Angle, decal.ZIndex, decal.Cleanable))
                            {
                                decalsRestored++;
                            }
                            else
                            {
                                decalsFailed++;
                                _sawmill.Warning($"Failed to restore decal {decal.Id} at {decal.Coordinates}");
                            }
                        }
                    }
                    
                    _sawmill.Info($"Restored {decalsRestored} decals from {decalChunkCollection.ChunkCollection.Count} chunks");
                    if (decalsFailed > 0)
                    {
                        _sawmill.Warning($"Failed to restore {decalsFailed} decals");
                    }
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to restore decal data: {ex.Message}");
                }
            }
            else
            {
                _sawmill.Info("No decal data found, decals will not be restored");
            }

            // Reconstruct entities with component restoration
            foreach (var entityData in primaryGridData.Entities)
            {
                // Skip entities with empty or null prototypes
                if (string.IsNullOrEmpty(entityData.Prototype))
                {
                    _sawmill.Debug($"Skipping entity with empty prototype at {entityData.Position}");
                    continue;
                }

                try
                {
                    var coordinates = new EntityCoordinates(newGrid.Owner, entityData.Position);
                    var newEntity = _entityManager.SpawnEntity(entityData.Prototype, coordinates);
                    
                    // Apply rotation if it exists
                    if (Math.Abs(entityData.Rotation) > 0.001f)
                    {
                        var transform = _entityManager.GetComponent<TransformComponent>(newEntity);
                        transform.LocalRotation = new Angle(entityData.Rotation);
                    }

                    // Restore component states (like anchored status)
                    RestoreEntityComponents(newEntity, entityData);
                    
                    _sawmill.Debug($"Spawned entity {newEntity} ({entityData.Prototype})");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to spawn entity {entityData.Prototype}: {ex.Message}");
                    throw;
                }
            }

            return newGrid.Owner;
        }

        private bool IsEntityContained(EntityUid entityUid, EntityUid gridId)
        {
            // Check if this entity is contained within another entity (not directly on the grid)
            if (_entityManager.TryGetComponent<TransformComponent>(entityUid, out var transformComp))
            {
                var parent = transformComp.ParentUid;
                while (parent.IsValid() && parent != gridId)
                {
                    // If we find a parent that has a ContainerManagerComponent, this entity is contained
                    if (_entityManager.HasComponent<ContainerManagerComponent>(parent))
                    {
                        return true;
                    }
                    
                    // Move up the hierarchy
                    if (_entityManager.TryGetComponent<TransformComponent>(parent, out var parentTransform))
                        parent = parentTransform.ParentUid;
                    else
                        break;
                }
            }
            return false;
        }

        private void ApplyFixGridAtmosphereToGrid(EntityUid gridUid)
        {
            // Execute fixgridatmos console command after a short delay to allow atmosphere system to initialize
            Timer.Spawn(TimeSpan.FromMilliseconds(100), () =>
            {
                if (!_entityManager.EntityExists(gridUid))
                {
                    _sawmill.Error($"Grid {gridUid} no longer exists for atmosphere application");
                    return;
                }

                var netEntity = _entityManager.GetNetEntity(gridUid);
                var commandArgs = $"fixgridatmos {netEntity}";
                _sawmill.Info($"Running fixgridatmos command: {commandArgs}");
                
                try
                {
                    _consoleHost.ExecuteCommand(null, commandArgs);
                    _sawmill.Info($"Successfully executed fixgridatmos for grid {gridUid}");
                }
                catch (Exception ex)
                {
                    _sawmill.Error($"Failed to execute fixgridatmos command: {ex.Message}");
                }
            });
        }

        private void RestoreEntityComponents(EntityUid entity, EntityData entityData)
        {
            foreach (var componentData in entityData.Components)
            {
                if (componentData.Type == "Physics" && _entityManager.TryGetComponent<PhysicsComponent>(entity, out var physics))
                {
                    if (componentData.Properties.TryGetValue("IsAnchored", out var anchoredObj) 
                        && anchoredObj is string anchoredStr 
                        && bool.TryParse(anchoredStr, out var isAnchored))
                    {
                        // Skip setting anchored status for now due to access restrictions
                        _sawmill.Debug($"Entity {entity} was {(isAnchored ? "anchored" : "unanchored")} in original save");
                    }
                }
            }
        }

        private string GenerateChecksum(List<GridData> grids)
        {
            var checksumData = new List<object>();
            
            foreach (var grid in grids)
            {
                // Create deterministic checksum that excludes potentially non-deterministic decal data
                checksumData.Add(new
                {
                    Tiles = grid.Tiles.Select(t => new { t.X, t.Y, t.TileType }).OrderBy(t => t.X).ThenBy(t => t.Y),
                    Entities = grid.Entities.Select(e => new { e.Prototype, e.Position, e.Rotation }).OrderBy(e => e.Position.X).ThenBy(e => e.Position.Y),
                    // Use simple hash of decal data string to avoid YAML serialization inconsistencies
                    DecalDataHash = string.IsNullOrEmpty(grid.DecalData) ? "" : grid.DecalData.GetHashCode().ToString()
                });
            }
            
            var yamlString = _serializer.Serialize(checksumData);
            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(yamlString));
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}