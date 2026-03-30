using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;

namespace S2FOW.Core;

public class EntityOwnershipResolver
{
    private const int MaxHandleEntriesBeforeReset = 2048;
    private const int MaxSceneNodeEntriesBeforeReset = 2048;
    private const int MaxTemporalOwnershipEntries = 256;
    // Tick-stamped dictionaries avoid clear-and-rebuild churn every frame.
    private readonly Dictionary<uint, (int Slot, int Tick)> _handleToSlot = new(512);
    private readonly Dictionary<nint, (int Slot, int Tick)> _sceneNodeOwnerSlots = new(512);

    // Temporal ownership: entities that lost their owner but should retain association
    // for a configurable number of ticks (e.g., dropped weapons after player death).
    private readonly Dictionary<int, (int Slot, int ExpiryTick)> _temporalOwnership = new(64);
    private int _temporalOwnershipDurationTicks;
    private int _lastFrameTick = int.MinValue;

    // Reused scratch list to avoid per-cull GC allocation.
    private readonly List<int> _expiredTemporalScratch = new(8);

    // Provide a callback to log exceptions using the parent's formatting
    private readonly Action<string, Exception, CEntityInstance?> _logException;

    public EntityOwnershipResolver(Action<string, Exception, CEntityInstance?> logException)
    {
        _logException = logException;
    }

    /// <summary>
    /// Sets the number of ticks to retain ownership for dropped weapons after death.
    /// </summary>
    public void SetTemporalOwnershipDuration(int ticks)
    {
        _temporalOwnershipDurationTicks = Math.Max(0, ticks);
    }

    public void Reset()
    {
        _handleToSlot.Clear();
        _sceneNodeOwnerSlots.Clear();
        _temporalOwnership.Clear();
        _lastFrameTick = int.MinValue;
    }

    public void BeginFrame(int currentTick)
    {
        if (currentTick == _lastFrameTick)
            return;

        _lastFrameTick = currentTick;

        // Same-tick entries are the only ones considered valid; stale generations can be dropped.
        if (_handleToSlot.Count > MaxHandleEntriesBeforeReset)
            _handleToSlot.Clear();

        if (_sceneNodeOwnerSlots.Count > MaxSceneNodeEntriesBeforeReset)
            _sceneNodeOwnerSlots.Clear();

        // Cull expired temporal ownership entries
        if (_temporalOwnership.Count > 0)
            CullExpiredTemporalOwnership(currentTick);
    }

    public void RegisterHandle(uint handleRaw, int slot, int currentTick)
    {
        if (handleRaw != 0 && handleRaw != Utilities.InvalidEHandleIndex)
            _handleToSlot[handleRaw] = (slot, currentTick);
    }

    public void RegisterSceneNodeOwner(nint sceneNodeHandle, int slot, int currentTick)
    {
        if (sceneNodeHandle != 0)
            _sceneNodeOwnerSlots[sceneNodeHandle] = (slot, currentTick);
    }

    public int FindPlayerSlotByKnownHandle(uint handleRaw, int currentTick)
    {
        if (handleRaw == 0 || handleRaw == Utilities.InvalidEHandleIndex)
            return -1;

        if (_handleToSlot.TryGetValue(handleRaw, out var data) && data.Tick == currentTick)
            return data.Slot;

        return -1;
    }

    public bool TryGetSceneNodeOwnerSlot(nint sceneNodeHandle, int currentTick, out int slot)
    {
        if (_sceneNodeOwnerSlots.TryGetValue(sceneNodeHandle, out var data) && data.Tick == currentTick)
        {
            slot = data.Slot;
            return true;
        }
        slot = -1;
        return false;
    }

    public int FindLinkedPlayerSlot(CBaseEntity entity, int currentTick, out nint unresolvedParentHandle)
    {
        unresolvedParentHandle = 0;

        int ownerSlot = FindPlayerSlotByKnownHandle(entity.OwnerEntity.Raw, currentTick);
        if (ownerSlot >= 0)
            return ownerSlot;

        int specialSlot = FindSpecialHandleLinkedPlayerSlot(entity, currentTick);
        if (specialSlot >= 0)
            return specialSlot;

        // Check temporal ownership (dropped weapons that recently lost their owner)
        int entityIndex = (int)entity.Index;
        if (entityIndex > 0 && TryGetTemporalOwnerSlot(entityIndex, currentTick, out int temporalSlot))
            return temporalSlot;

        var sceneNode = entity.CBodyComponent?.SceneNode;
        if (sceneNode == null)
            return -1;

        var parent = sceneNode.PParent;
        int depth = 0;

        while (parent != null && depth++ < 16)
        {
            nint parentHandle = parent.Handle;
            if (TryGetSceneNodeOwnerSlot(parentHandle, currentTick, out int slot))
                return slot;

            if (unresolvedParentHandle == 0)
                unresolvedParentHandle = parentHandle;

            parent = parent.PParent;
        }

        return -1;
    }

    /// <summary>
    /// Records temporal ownership for an entity that is about to lose its owner
    /// (e.g., weapons dropped on player death). The entity will remain associated
    /// with the owner for the configured duration.
    /// </summary>
    public void RecordTemporalOwnership(int entityIndex, int ownerSlot, int currentTick)
    {
        if (entityIndex <= 0 || !FowConstants.IsValidSlot(ownerSlot) || _temporalOwnershipDurationTicks <= 0)
            return;

        if (_temporalOwnership.Count >= MaxTemporalOwnershipEntries)
            return;

        _temporalOwnership[entityIndex] = (ownerSlot, currentTick + _temporalOwnershipDurationTicks);
    }

    /// <summary>
    /// Removes temporal ownership for an entity (e.g., when picked up by another player).
    /// </summary>
    public void RemoveTemporalOwnership(int entityIndex)
    {
        _temporalOwnership.Remove(entityIndex);
    }

    public bool TryGetTemporalOwnerSlot(int entityIndex, int currentTick, out int slot)
    {
        if (_temporalOwnership.TryGetValue(entityIndex, out var data) && currentTick < data.ExpiryTick)
        {
            slot = data.Slot;
            return true;
        }
        slot = -1;
        return false;
    }

    private void CullExpiredTemporalOwnership(int currentTick)
    {
        _expiredTemporalScratch.Clear();
        foreach (var kvp in _temporalOwnership)
        {
            if (currentTick >= kvp.Value.ExpiryTick)
                _expiredTemporalScratch.Add(kvp.Key);
        }

        for (int i = 0; i < _expiredTemporalScratch.Count; i++)
            _temporalOwnership.Remove(_expiredTemporalScratch[i]);
    }

    private int FindSpecialHandleLinkedPlayerSlot(CBaseEntity entity, int currentTick)
    {
        try
        {
            if (TryFindSceneEntitySlot(entity, currentTick, out int slot) ||
                TryFindInstancedSceneSlot(entity, currentTick, out slot) ||
                TryFindPlayerPingSlot(entity, currentTick, out slot) ||
                TryFindInstructorEventSlot(entity, currentTick, out slot) ||
                TryFindDogtagsSlot(entity, currentTick, out slot) ||
                TryFindLogicPlayerProxySlot(entity, currentTick, out slot) ||
                TryFindPhysBoxSlot(entity, currentTick, out slot) ||
                TryFindValueRemapperSlot(entity, currentTick, out slot) ||
                TryFindHostageLeaderSlot(entity, currentTick, out slot))
            {
                return slot;
            }
        }
        catch (Exception ex)
        {
            _logException("special reverse-link dispatch", ex, entity);
        }

        return -1;
    }

    // Centralized accessors keep the scene-entity target scan compact.
    private static readonly Func<CSceneEntity, uint>[] SceneEntityTargetAccessors = [
        s => s.HTarget1.Raw, s => s.HTarget2.Raw, s => s.HTarget3.Raw, s => s.HTarget4.Raw,
        s => s.HTarget5.Raw, s => s.HTarget6.Raw, s => s.HTarget7.Raw, s => s.HTarget8.Raw
    ];

    private bool TryFindSceneEntitySlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = -1;
        if (entity is not CSceneEntity scene) return false;

        slot = FindPlayerSlotByKnownHandle(scene.Actor.Raw, currentTick);
        if (slot >= 0) return true;

        var actorList = scene.ActorList;
        for (int i = 0; i < actorList.Count; i++)
        {
            slot = FindPlayerSlotByKnownHandle(actorList[i].Raw, currentTick);
            if (slot >= 0) return true;
        }

        slot = FindPlayerSlotByKnownHandle(scene.Activator.Raw, currentTick);
        if (slot >= 0) return true;

        foreach (var accessor in SceneEntityTargetAccessors)
        {
            slot = FindPlayerSlotByKnownHandle(accessor(scene), currentTick);
            if (slot >= 0) return true;
        }

        var removeActorList = scene.RemoveActorList;
        for (int i = 0; i < removeActorList.Count; i++)
        {
            slot = FindPlayerSlotByKnownHandle(removeActorList[i].Raw, currentTick);
            if (slot >= 0) return true;
        }

        slot = -1;
        return false;
    }

    private bool TryFindInstancedSceneSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = -1;
        if (entity is not CInstancedSceneEntity instancedScene) return false;

        slot = FindPlayerSlotByKnownHandle(instancedScene.Owner.Raw, currentTick);
        return slot >= 0 || (slot = FindPlayerSlotByKnownHandle(instancedScene.Target.Raw, currentTick)) >= 0;
    }

    private bool TryFindPlayerPingSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = entity is CPlayerPing ping ? FindPlayerSlotByKnownHandle(ping.Player.Raw, currentTick) : -1;
        return slot >= 0;
    }

    private bool TryFindInstructorEventSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = entity is CInstructorEventEntity instructorEvent ? FindPlayerSlotByKnownHandle(instructorEvent.TargetPlayer.Raw, currentTick) : -1;
        return slot >= 0;
    }

    private bool TryFindDogtagsSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = -1;
        if (entity is not CItemDogtags dogtags) return false;

        slot = FindPlayerSlotByKnownHandle(dogtags.OwningPlayer.Raw, currentTick);
        return slot >= 0 || (slot = FindPlayerSlotByKnownHandle(dogtags.KillingPlayer.Raw, currentTick)) >= 0;
    }

    private bool TryFindLogicPlayerProxySlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = entity is CLogicPlayerProxy playerProxy ? FindPlayerSlotByKnownHandle(playerProxy.Player.Raw, currentTick) : -1;
        return slot >= 0;
    }

    private bool TryFindPhysBoxSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = entity is CPhysBox physBox ? FindPlayerSlotByKnownHandle(physBox.CarryingPlayer.Raw, currentTick) : -1;
        return slot >= 0;
    }

    private bool TryFindValueRemapperSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = entity is CPointValueRemapper valueRemapper ? FindPlayerSlotByKnownHandle(valueRemapper.UsingPlayer.Raw, currentTick) : -1;
        return slot >= 0;
    }

    /// <summary>
    /// Resolves hostage entities following a player. CHostage.Leader points to the
    /// player pawn that is carrying/leading the hostage. If a hostage follows a hidden
    /// player, the hostage entity should be hidden too to prevent information leaks.
    /// Only relevant on hostage maps (cs_office, cs_italy, etc.).
    /// </summary>
    private bool TryFindHostageLeaderSlot(CBaseEntity entity, int currentTick, out int slot)
    {
        slot = -1;
        if (entity is not CHostage hostage) return false;

        slot = FindPlayerSlotByKnownHandle(hostage.Leader.Raw, currentTick);
        return slot >= 0;
    }
}
