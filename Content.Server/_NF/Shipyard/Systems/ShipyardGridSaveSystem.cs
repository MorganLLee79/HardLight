using Content.Server._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared._NF.Shipyard.Events;
using Content.Shared.Shuttles.Save;
using Content.Server.Maps;
using Content.Server.Power.EntitySystems;
using Content.Shared.Access.Components;
using Robust.Server.GameObjects;
using Robust.Server.Player;
using Robust.Shared.ContentPack;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Utility;
using System.Numerics;
using Robust.Shared.Containers;
using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Content.Server._NF.Shipyard.Systems;

/// <summary>
/// System for saving ships using the same pattern as the shipyard map loader system.
/// Simplified approach that mirrors ship purchasing: move to shipyard map, save, cleanup.
/// </summary>
public sealed class ShipyardGridSaveSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly IResourceManager _resourceManager = default!;

    private ISawmill _sawmill = default!;
    private MapLoaderSystem _mapLoader = default!;
    private ShipyardSystem _shipyardSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        _sawmill = Logger.GetSawmill("shipyard.gridsave");
        _mapLoader = _entitySystemManager.GetEntitySystem<MapLoaderSystem>();
        _shipyardSystem = _entitySystemManager.GetEntitySystem<ShipyardSystem>();

        SubscribeLocalEvent<ShipyardConsoleComponent, ShipyardConsoleSaveMessage>(OnSaveShipMessage);
    }

    private void OnSaveShipMessage(EntityUid consoleUid, ShipyardConsoleComponent component, ShipyardConsoleSaveMessage args)
    {
        if (args.Actor is not { Valid: true } player)
            return;

        if (component.TargetIdSlot.ContainerSlot?.ContainedEntity is not { Valid: true } targetId)
        {
            _sawmill.Warning("No ID card in shipyard console slot");
            return;
        }

        if (!_entityManager.TryGetComponent<ShuttleDeedComponent>(targetId, out var deed))
        {
            _sawmill.Warning("ID card does not have a shuttle deed");
            return;
        }

        if (deed.ShuttleUid == null || !_entityManager.TryGetEntity(deed.ShuttleUid.Value, out var shuttleUid))
        {
            _sawmill.Warning("Shuttle deed does not reference a valid shuttle");
            return;
        }

        if (!_entityManager.TryGetComponent<MapGridComponent>(shuttleUid.Value, out var gridComponent))
        {
            _sawmill.Warning("Shuttle entity is not a valid grid");
            return;
        }

        if (!_playerManager.TryGetSessionByEntity(player, out var playerSession))
        {
            _sawmill.Warning("Could not get player session");
            return;
        }

        var shipName = deed.ShuttleName ?? "Unknown_Ship";
        _sawmill.Info($"Starting simplified ship save for {shipName} owned by {playerSession.Name}");

        // Use the simplified save method that mirrors ship purchasing
        var success = TrySaveShipToExports(shuttleUid.Value, shipName, playerSession);

        if (success)
        {
            // Clean up the deed after successful save
            _entityManager.RemoveComponent<ShuttleDeedComponent>(targetId);
            RemoveAllShuttleDeeds(shuttleUid.Value);
            _sawmill.Info($"Successfully saved and removed ship {shipName}");
        }
        else
        {
            _sawmill.Error($"Failed to save ship {shipName}");
        }
    }

    /// <summary>
    /// Simplified ship saving that mirrors the ship purchasing pattern.
    /// Step 1: Move to shipyard map, Step 2: Minimal cleaning, Step 3: Save and cleanup.
    /// </summary>
    public bool TrySaveShipToExports(EntityUid gridUid, string shipName, ICommonSession playerSession)
    {
        if (!_entityManager.HasComponent<MapGridComponent>(gridUid))
        {
            _sawmill.Error($"Entity {gridUid} is not a valid grid");
            return false;
        }

        try
        {
            _sawmill.Info($"Saving ship '{shipName}' using simplified process (mirrors ship purchasing)");

            // Step 1: Move ship to shipyard map (same as ship purchases)
            if (!MoveShipToShipyardMap(gridUid))
            {
                _sawmill.Error("Failed to move ship to shipyard map");
                return false;
            }

            // Step 2: Apply minimal cleaning (only session-specific components)
            ApplyMinimalCleaning(gridUid);

            // Step 3: Save ship to exports using MapLoaderSystem (same as ship loading)
            var fileName = $"{shipName}.yml";
            var exportPath = new ResPath("/Exports") / fileName;
            
            if (!_mapLoader.TrySaveGrid(gridUid, exportPath))
            {
                _sawmill.Error($"Failed to save grid to {exportPath}");
                return false;
            }

            // Step 4: Send saved ship data to client (maintains compatibility)
            SendShipDataToClient(exportPath, shipName, playerSession);

            // Step 5: Clean up original grid (same as after ship purchase)
            _entityManager.DeleteEntity(gridUid);

            _sawmill.Info($"Successfully saved ship '{shipName}' to exports using simplified process");
            
            // Fire success event
            var gridSavedEvent = new ShipSavedEvent
            {
                GridUid = gridUid,
                ShipName = shipName,
                PlayerUserId = playerSession.UserId.ToString(),
                PlayerSession = playerSession
            };
            RaiseLocalEvent(gridSavedEvent);

            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Exception during simplified ship save: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Compatibility method for ShipSaveSystem - async wrapper around TrySaveShipToExports.
    /// Maintains compatibility with existing code that expects the old async interface.
    /// </summary>
    public async Task<bool> TrySaveGridAsShip(EntityUid gridUid, string shipName, string playerUserId, ICommonSession playerSession)
    {
        _sawmill.Info($"TrySaveGridAsShip called for compatibility (ship: {shipName}, player: {playerUserId})");
        
        // Call our simplified synchronous method
        return await Task.Run(() => TrySaveShipToExports(gridUid, shipName, playerSession));
    }

    /// <summary>
    /// Moves ship to shipyard map, following the exact same pattern as ship purchases.
    /// </summary>
    private bool MoveShipToShipyardMap(EntityUid gridUid)
    {
        try
        {
            // Ensure shipyard map exists (same check as ship purchases)
            if (_shipyardSystem.ShipyardMap == null)
            {
                _sawmill.Error("ShipyardMap is not available");
                return false;
            }

            // Move the grid to the shipyard map at a safe position (same as TryAddShuttle)
            var gridTransform = _entityManager.GetComponent<TransformComponent>(gridUid);
            var shipyardMapUid = _mapManager.GetMapEntityId(_shipyardSystem.ShipyardMap.Value);
            
            // Use same offset pattern as ship purchasing to avoid collisions
            var offset = new Vector2(500f, 1f);
            _transformSystem.SetCoordinates(gridUid, new EntityCoordinates(shipyardMapUid, offset));
            _transformSystem.SetLocalRotation(gridUid, Angle.Zero);

            _sawmill.Info($"Moved ship grid {gridUid} to shipyard map at {offset} (same as ship purchases)");
            return true;
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to move ship to shipyard map: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Minimal cleaning - only removes components that break serialization.
    /// Much simpler than the complex 5-step cleaning process.
    /// </summary>
    private void ApplyMinimalCleaning(EntityUid gridUid)
    {
        try
        {
            _sawmill.Info("Applying minimal cleaning (session-specific components only)");

            var allEntities = new HashSet<EntityUid>();

            // Get all entities on the grid
            if (_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            {
                var gridBounds = grid.LocalAABB;
                var lookupSystem = _entitySystemManager.GetEntitySystem<EntityLookupSystem>();
                foreach (var entity in lookupSystem.GetEntitiesIntersecting(gridUid, gridBounds))
                {
                    if (entity != gridUid)
                        allEntities.Add(entity);
                }
            }

            var componentsRemoved = 0;

            // Only remove components that actually break serialization
            foreach (var entity in allEntities)
            {
                if (!_entityManager.EntityExists(entity))
                    continue;

                // Remove session-specific components (these don't serialize properly)
                if (_entityManager.RemoveComponent<ActorComponent>(entity))
                    componentsRemoved++;
                if (_entityManager.RemoveComponent<EyeComponent>(entity))
                    componentsRemoved++;

                // Keep everything else - physics, power, atmospheric, etc.
                // Let the ship function normally when loaded
            }

            _sawmill.Info($"Minimal cleaning complete: removed {componentsRemoved} session components from {allEntities.Count} entities");
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Error during minimal cleaning: {ex}");
        }
    }

    /// <summary>
    /// Sends saved ship data to client for local storage (maintains compatibility).
    /// </summary>
    private void SendShipDataToClient(ResPath exportPath, string shipName, ICommonSession playerSession)
    {
        try
        {
            using var fileStream = _resourceManager.UserData.OpenRead(exportPath);
            using var reader = new StreamReader(fileStream);
            var yamlContent = reader.ReadToEnd();

            var saveMessage = new SendShipSaveDataClientMessage(shipName, yamlContent);
            RaiseNetworkEvent(saveMessage, playerSession);

            _sawmill.Info($"Sent ship data '{shipName}' to client {playerSession.Name}");

            // Clean up server file
            TryDeleteServerFile(exportPath);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Failed to send ship data to client: {ex}");
        }
    }

    /// <summary>
    /// Clean up server-side YAML file after sending to client.
    /// </summary>
    private void TryDeleteServerFile(ResPath filePath)
    {
        try
        {
            _resourceManager.UserData.Delete(filePath);
            _sawmill.Info($"Deleted server-side file {filePath}");
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Could not delete server-side file {filePath}: {ex}");
        }
    }

    /// <summary>
    /// Removes all shuttle deed components that reference the specified shuttle.
    /// </summary>
    private void RemoveAllShuttleDeeds(EntityUid shuttleUid)
    {
        var query = _entityManager.EntityQueryEnumerator<ShuttleDeedComponent>();
        var deedsToRemove = new List<EntityUid>();

        while (query.MoveNext(out var entityUid, out var deed))
        {
            if (deed.ShuttleUid != null && 
                _entityManager.TryGetEntity(deed.ShuttleUid.Value, out var deedShuttleEntity) && 
                deedShuttleEntity == shuttleUid)
            {
                deedsToRemove.Add(entityUid);
            }
        }

        foreach (var deedEntity in deedsToRemove)
        {
            _entityManager.RemoveComponent<ShuttleDeedComponent>(deedEntity);
            _sawmill.Info($"Removed shuttle deed from entity {deedEntity}");
        }
    }

    /// <summary>
    /// Legacy method kept for integration test compatibility.
    /// This now uses the simplified cleaning approach.
    /// </summary>
    public void CleanGridForSaving(EntityUid gridUid)
    {
        _sawmill.Info($"Using simplified grid cleanup for {gridUid} (legacy method)");
        ApplyMinimalCleaning(gridUid);
    }
}
