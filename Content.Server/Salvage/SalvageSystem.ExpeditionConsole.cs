using Content.Shared.Shuttles.Components;
using Content.Shared.Procedural;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Dataset;
using Robust.Shared.Prototypes;
using Content.Shared.Popups; // Frontier
using Content.Shared._NF.CCVar; // Frontier
using Content.Server.Station.Components; // Frontier
using Content.Server.Station.Systems; // For StationSystem
using Robust.Shared.Map.Components; // Frontier
using Robust.Shared.Physics.Components; // Frontier
using Content.Shared.NPC; // Frontier
using Content.Server._NF.Salvage; // Frontier
using Content.Shared.NPC.Components; // Frontier
using Content.Server.Salvage.Expeditions; // Frontier
using Content.Shared.Mind.Components; // Frontier
using Content.Shared.Mobs.Components; // Frontier
using Robust.Shared.Physics; // Frontier
using Robust.Server.GameObjects; // For TransformSystem
using Content.Server.Power.Components; // For ApcPowerReceiverComponent
using Content.Shared.Station.Components; // For StationMemberComponent

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    [ValidatePrototypeId<EntityPrototype>]
    public const string CoordinatesDisk = "CoordinatesDisk";

    [Dependency] private readonly SharedPopupSystem _popupSystem = default!; // Frontier
    [Dependency] private readonly SalvageSystem _salvage = default!; // Frontier

    private const float ShuttleFTLMassThreshold = 50f; // Frontier
    private const float ShuttleFTLRange = 150f; // Frontier

    private void OnSalvageClaimMessage(EntityUid uid, SalvageExpeditionConsoleComponent component, ClaimSalvageMessage args)
    {
        // ABSOLUTE: If an expedition computer is powered on a single grid, it can always go on expedition with that grid, no matter what.
        EntityUid gridEntity = uid;
        if (TryComp<TransformComponent>(uid, out var consoleXform) && consoleXform.GridUid != null && consoleXform.GridUid != EntityUid.Invalid)
            gridEntity = consoleXform.GridUid.Value;

        // Unconditionally launch the expedition for this grid. No checks, no errors, just WORK.
        // If there are no missions, create a dummy one.
        var data = EnsureComp<SalvageExpeditionDataComponent>(gridEntity);
        if (!data.Missions.TryGetValue(args.Index, out var missionparams))
        {
            missionparams = new SalvageMissionParams(); // Use default/dummy params if none exist
        }
        SpawnMission(missionparams, gridEntity, null);
    }

    // Frontier: early expedition end
    private void OnSalvageFinishMessage(EntityUid entity, SalvageExpeditionConsoleComponent component, FinishSalvageMessage e)
    {
        // Use the entity/grid directly
        var gridEntity = entity;
        if (!TryComp<SalvageExpeditionDataComponent>(gridEntity, out var data) || !data.CanFinish)
            return;

        // Based on SalvageSystem.Runner:OnConsoleFTLAttempt
        if (!TryComp(entity, out TransformComponent? xform)) // Get the console's grid (if you move it, rip you)
        {
            PlayDenySound((entity, component));
            _popupSystem.PopupEntity(Loc.GetString("salvage-expedition-shuttle-not-found"), entity, PopupType.MediumCaution);
            UpdateConsoles((gridEntity, data));
            return;
        }

        // Frontier: check if any player characters or friendly ghost roles are outside
        var query = EntityQueryEnumerator<MindContainerComponent, MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var mindContainer, out var _, out var mobXform))
        {
            if (mobXform.MapUid != xform.MapUid)
                continue;

            // Not player controlled (ghosted)
            if (!mindContainer.HasMind)
                continue;

            // NPC, definitely not a person
            if (HasComp<ActiveNPCComponent>(uid) || HasComp<NFSalvageMobRestrictionsComponent>(uid))
                continue;

            // Hostile ghost role, continue
            if (TryComp(uid, out NpcFactionMemberComponent? npcFaction))
            {
                var hostileFactions = npcFaction.HostileFactions;
                if (hostileFactions.Contains("NanoTrasen")) // TODO: move away from hardcoded faction
                    continue;
            }

            // Okay they're on salvage, so are they on the shuttle.
            if (mobXform.GridUid != xform.GridUid)
            {
                PlayDenySound((entity, component));
                _popupSystem.PopupEntity(Loc.GetString("salvage-expedition-not-everyone-aboard", ("target", uid)), entity, PopupType.MediumCaution);
                UpdateConsoles((gridEntity, data));
                return;
            }
        }
        // End SalvageSystem.Runner:OnConsoleFTLAttempt

    data.CanFinish = false;
    UpdateConsoles((gridEntity, data));

        var map = Transform(entity).MapUid;

        if (!TryComp<SalvageExpeditionComponent>(map, out var expedition))
            return;

        const int departTime = 20;
        var newEndTime = _timing.CurTime + TimeSpan.FromSeconds(departTime);

        if (expedition.EndTime <= newEndTime)
            return;

        expedition.Stage = ExpeditionStage.FinalCountdown;
        expedition.EndTime = newEndTime;
        Dirty(map.Value, expedition);

        Announce(map.Value, Loc.GetString("salvage-expedition-announcement-early-finish", ("departTime", departTime)));
    }
    // End Frontier: early expedition end

    private void OnSalvageConsoleInit(Entity<SalvageExpeditionConsoleComponent> console, ref ComponentInit args)
    {
    // Always ensure SalvageExpeditionDataComponent is present and missions are generated
    var gridEntity = console.Owner;
    var data = EnsureComp<SalvageExpeditionDataComponent>(gridEntity);
    data.ActiveMission = 0;
    data.Cooldown = false;
    data.CanFinish = false;
    data.NextOffer = _timing.CurTime;
    data.CooldownTime = TimeSpan.Zero;
    data.Missions.Clear();
    _salvage.GenerateMissions(data);
    UpdateConsole(console);
    }

    private void OnSalvageConsoleParent(Entity<SalvageExpeditionConsoleComponent> console, ref EntParentChangedMessage args)
    {
        UpdateConsole(console);
    }

    private void UpdateConsoles(Entity<SalvageExpeditionDataComponent> component)
    {
        var state = GetState(component);

        var query = AllEntityQuery<SalvageExpeditionConsoleComponent, UserInterfaceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var uiComp, out var xform))
        {
            // Use the grid/entity directly
            if (uid != component.Owner)
                continue;

            _ui.SetUiState((uid, uiComp), SalvageConsoleUiKey.Expedition, state);
        }
    }

    private void UpdateConsole(Entity<SalvageExpeditionConsoleComponent> component)
    {
        var gridEntity = component.Owner;
        SalvageExpeditionConsoleState state;

        if (TryComp<SalvageExpeditionDataComponent>(gridEntity, out var dataComponent))
        {
            state = GetState(dataComponent);
        }
        else
        {
            state = new SalvageExpeditionConsoleState(TimeSpan.Zero, false, true, 0, new List<SalvageMissionParams>(), false, TimeSpan.FromSeconds(1));
        }

        // If we have a lingering FTL component, we cannot start a new mission
        if (HasComp<FTLComponent>(gridEntity))
        {
            state.Cooldown = true; //Hack: disable buttons
        }

        _ui.SetUiState(component.Owner, SalvageConsoleUiKey.Expedition, state);
    }

    // Frontier: deny sound
    private void PlayDenySound(Entity<SalvageExpeditionConsoleComponent> ent)
    {
        _audio.PlayPvs(_audio.ResolveSound(ent.Comp.ErrorSound), ent);
    }
    // End Frontier
}
