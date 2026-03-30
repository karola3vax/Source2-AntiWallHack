using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

public class PlayerStateCache
{
    private const int WarningIntervalTicks = 128;
    private const int MinReverseLinkRescanBudgetPerFrame = 32;

    private struct UnresolvedEntityInfo
    {
        public nint ParentHandle;
        public int FirstSeenTick;
    }

    private readonly PlayerSnapshot[] _snapshots = new PlayerSnapshot[FowConstants.MaxSlots];
    private readonly int[,] _associatedEntities = new int[FowConstants.MaxSlots, PlayerSnapshot.MaxAssociatedEntities];
    
    private readonly EntityOwnershipResolver _entityOwnershipResolver;
    private readonly SceneNodeTraverser _sceneNodeTraverser;
    private readonly SceneNodeTraverser.AddEntityCallback _childSceneEntityCallback;

    private readonly int[] _activeSlots = new int[FowConstants.MaxSlots];
    private readonly int[] _terroristSlots = new int[FowConstants.MaxSlots];
    private readonly int[] _counterTerroristSlots = new int[FowConstants.MaxSlots];
    private readonly HashSet<int> _trackedEntityIndices = new(1024);
    private readonly HashSet<int> _dirtyEntityIndices = new(256);
    private readonly Dictionary<int, int> _entityOwnerSlotCache = new(512);
    private readonly Dictionary<int, UnresolvedEntityInfo> _unresolvedParentByEntityIndex = new(256);
    private readonly HashSet<int> _fullRescanSeenEntityIndices = new(1024);
    private readonly List<int> _dirtyEntityProcessingScratch = new(256);
    private readonly List<int> _staleEntityCleanupScratch = new(128);
    private readonly Dictionary<string, int> _nextWarningTicksByKey = new();
    private readonly HashSet<int>[] _reverseLinkedEntityIndicesBySlot = new HashSet<int>[FowConstants.MaxSlots];

    private S2FOWConfig _config;
    private int _activeSlotCount;
    private int _terroristSlotCount;
    private int _counterTerroristSlotCount;
    private int _lastReverseLinkFullRescanTick = int.MinValue;
    private IntPtr _fullReverseLinkRescanCursor = IntPtr.Zero;
    private bool _fullReverseLinkRescanInProgress;
    private long _reverseLinkDereferenceExceptionCount;
    private long _suppressedWarningCount;
    private int _childSceneEntityCurrentTick;
    private int _childSceneEntityWriteIndex;

    public PlayerStateCache(S2FOWConfig config)
    {
        _config = config;
        for (int i = 0; i < _reverseLinkedEntityIndicesBySlot.Length; i++)
            _reverseLinkedEntityIndicesBySlot[i] = new HashSet<int>(8);

        _entityOwnershipResolver = new EntityOwnershipResolver(LogSuppressedException);
        _entityOwnershipResolver.SetTemporalOwnershipDuration(config.AntiWallhack.BlockDroppedWeaponESPDurationTicks);
        _sceneNodeTraverser = new SceneNodeTraverser();
        _childSceneEntityCallback = AddChildSceneEntity;
    }

    public ReadOnlySpan<PlayerSnapshot> Snapshots => _snapshots;
    public ReadOnlySpan<int> ActiveSlots => _activeSlots.AsSpan(0, _activeSlotCount);
    public ReadOnlySpan<int> TerroristSlots => _terroristSlots.AsSpan(0, _terroristSlotCount);
    public ReadOnlySpan<int> CounterTerroristSlots => _counterTerroristSlots.AsSpan(0, _counterTerroristSlotCount);
    public long ReverseLinkDereferenceExceptionCount => _reverseLinkDereferenceExceptionCount;
    public long SuppressedWarningCount => _suppressedWarningCount;
    public int TrackedEntityCount => _trackedEntityIndices.Count;
    public int DirtyEntityCount => _dirtyEntityIndices.Count;
    public int UnresolvedParentCount => _unresolvedParentByEntityIndex.Count;
    public bool FullReverseLinkRescanInProgress => _fullReverseLinkRescanInProgress;

    public void Configure(S2FOWConfig config)
    {
        _config = config;
        _entityOwnershipResolver.SetTemporalOwnershipDuration(config.AntiWallhack.BlockDroppedWeaponESPDurationTicks);
        _sceneNodeTraverser.Configure(config.Performance.MaxSceneTraversalNodes, config.Performance.MaxSceneTraversalDepth);
    }

    public void CollectUnresolvedEntitiesToHide(List<int> output, int currentTick, bool strictMode)
    {
        output.Clear();

        if (strictMode)
        {
            foreach ((int entityIndex, _) in _unresolvedParentByEntityIndex)
                output.Add(entityIndex);

            return;
        }

        int hideAfterTicks = _config.Performance.HideUnresolvedEntitiesAfterTicks;
        if (hideAfterTicks <= 0 || _unresolvedParentByEntityIndex.Count == 0)
            return;

        foreach ((int entityIndex, UnresolvedEntityInfo info) in _unresolvedParentByEntityIndex)
        {
            if (currentTick - info.FirstSeenTick >= hideAfterTicks)
                output.Add(entityIndex);
        }
    }

    public int GetAssociatedEntity(int slot, int index)
    {
        return _associatedEntities[slot, index];
    }

    public bool ShouldRevealDroppedWeaponToObserver(int entityIndex, in PlayerSnapshot observer, int currentTick)
    {
        float revealDistance = _config.AntiWallhack.DroppedWeaponRevealDistance;
        if (entityIndex <= 0 || revealDistance <= 0.0f)
            return false;

        if (!_entityOwnershipResolver.TryGetTemporalOwnerSlot(entityIndex, currentTick, out _))
            return false;

        var weapon = Utilities.GetEntityFromIndex<CBasePlayerWeapon>(entityIndex);
        if (weapon == null || !weapon.IsValid)
            return false;

        // If the weapon is still owned/held, keep normal owner-based hiding behavior.
        if (weapon.OwnerEntity.Raw != 0 && weapon.OwnerEntity.Raw != Utilities.InvalidEHandleIndex)
            return false;

        // Only reveal truly dropped world weapons. If the weapon is still parented
        // under another scene node (for example a dying pawn / ragdoll chain),
        // letting it transmit can cause the parent side to reappear client-side.
        var sceneNode = weapon.CBodyComponent?.SceneNode;
        if (sceneNode?.PParent != null)
            return false;

        var absOrigin = weapon.AbsOrigin;
        if (absOrigin == null)
            return false;

        float revealDistanceSqr = revealDistance * revealDistance;
        return VectorMath.DistanceSquared(
            observer.EyePosX, observer.EyePosY, observer.EyePosZ,
            absOrigin.X, absOrigin.Y, absOrigin.Z) <= revealDistanceSqr;
    }

    /// <summary>
    /// Records temporal ownership for all weapon entities associated with a dying player.
    /// This ensures dropped weapons remain hidden for a configurable duration after death,
    /// preventing wallhack users from seeing death locations via dropped weapon ESP.
    /// </summary>
    public void RecordTemporalOwnershipForDeath(int slot, int currentTick)
    {
        if (!FowConstants.IsValidSlot(slot) || _config.AntiWallhack.BlockDroppedWeaponESPDurationTicks <= 0)
            return;

        ref readonly var snap = ref _snapshots[slot];
        if (!snap.IsValid)
            return;

        // Record all associated entities (weapons, wearables) for temporal ownership
        int count = snap.AssociatedEntityCount;
        for (int i = 0; i < count; i++)
        {
            int entityIndex = _associatedEntities[slot, i];
            if (entityIndex <= 0)
                continue;

            var entity = Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
            if (entity == null || !entity.IsValid || !ShouldTrackTemporalOwnership(entity))
                continue;

            _entityOwnershipResolver.RecordTemporalOwnership(entityIndex, slot, currentTick);
        }

        // Also record reverse-linked world weapons that were resolved to this slot.
        var reverseLinked = _reverseLinkedEntityIndicesBySlot[slot];
        foreach (int entityIndex in reverseLinked)
        {
            if (entityIndex <= 0)
                continue;

            var entity = Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
            if (entity == null || !entity.IsValid || !ShouldTrackTemporalOwnership(entity))
                continue;

            _entityOwnershipResolver.RecordTemporalOwnership(entityIndex, slot, currentTick);
        }
    }

    public void MarkEntityDirty(CEntityInstance? entity)
    {
        if (entity == null || !entity.IsValid || entity.Index <= 0)
            return;

        MarkEntityDirty((int)entity.Index);
    }

    public void MarkEntityDirty(int entityIndex)
    {
        if (entityIndex <= 0)
            return;

        _trackedEntityIndices.Add(entityIndex);
        _dirtyEntityIndices.Add(entityIndex);
    }

    public void OnEntityDeleted(CEntityInstance? entity)
    {
        if (entity == null || entity.Index <= 0)
            return;

        OnEntityDeleted((int)entity.Index);
    }

    public void OnEntityDeleted(int entityIndex)
    {
        if (entityIndex <= 0)
            return;

        RemoveTrackedEntity(entityIndex);
    }

    public void ResetTracking()
    {
        _entityOwnershipResolver.Reset();
        _trackedEntityIndices.Clear();
        _dirtyEntityIndices.Clear();
        _entityOwnerSlotCache.Clear();
        _unresolvedParentByEntityIndex.Clear();
        _fullRescanSeenEntityIndices.Clear();
        _dirtyEntityProcessingScratch.Clear();
        _staleEntityCleanupScratch.Clear();
        _nextWarningTicksByKey.Clear();
        _lastReverseLinkFullRescanTick = int.MinValue;
        _fullReverseLinkRescanCursor = IntPtr.Zero;
        _fullReverseLinkRescanInProgress = false;
        _reverseLinkDereferenceExceptionCount = 0;
        _suppressedWarningCount = 0;

        for (int i = 0; i < _reverseLinkedEntityIndicesBySlot.Length; i++)
            _reverseLinkedEntityIndicesBySlot[i].Clear();
    }

    public void BuildSnapshots(List<CCSPlayerController> players, int currentTick)
    {
        _entityOwnershipResolver.BeginFrame(currentTick);
        ResetActiveSlots();
        // Do not clear the resolver dictionaries here; tick-stamping already invalidates stale entries.
        _terroristSlotCount = 0;
        _counterTerroristSlotCount = 0;

        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            if (!controller.IsValid)
                continue;

            int slot = controller.Slot;
            if ((uint)slot >= FowConstants.MaxSlots)
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var absOrigin = pawn.AbsOrigin;
            if (absOrigin == null)
                continue;

            ref var snap = ref _snapshots[slot];
            snap.Slot = slot;
            snap.IsValid = true;
            snap.IsAlive = pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
            snap.Team = controller.Team;
            snap.IsBot = controller.IsBot;
            snap.ControllerEntityIndex = controller.Index > 0 ? (int)controller.Index : 0;
            snap.HasControllerEntity = controller.Index > 0;
            snap.PawnEntityIndex = pawn.Index;
            snap.PosX = absOrigin.X;
            snap.PosY = absOrigin.Y;
            snap.PosZ = absOrigin.Z;

            var viewOffset = pawn.ViewOffset;
            snap.ViewOffsetZ = viewOffset.Z;
            snap.EyePosX = absOrigin.X + viewOffset.X;
            snap.EyePosY = absOrigin.Y + viewOffset.Y;
            snap.EyePosZ = absOrigin.Z + viewOffset.Z;

            var eyeAngles = pawn.EyeAngles;
            snap.Pitch = eyeAngles?.X ?? 0.0f;
            snap.Yaw = eyeAngles?.Y ?? pawn.AbsRotation?.Y ?? 0.0f;

            var vel = pawn.AbsVelocity;
            if (vel != null)
            {
                snap.VelX = vel.X;
                snap.VelY = vel.Y;
                snap.VelZ = vel.Z;
            }

            snap.WeaponMaxSpeed = TryGetWeaponMaxSpeed(pawn);
            snap.IsScoped = pawn.IsScoped;

            var movementServices = pawn.MovementServices?.As<CCSPlayer_MovementServices>();
            if (movementServices != null)
                snap.DuckAmount = Math.Clamp(movementServices.DuckAmount, 0.0f, 1.0f);

            snap.IsOnGround = (pawn.Flags & (uint)Flags_t.FL_ONGROUND) != 0;

            var collision = pawn.Collision;
            if (collision != null)
            {
                var mins = collision.Mins;
                var maxs = collision.Maxs;
                if (mins != null)
                {
                    snap.MinsX = mins.X;
                    snap.MinsY = mins.Y;
                    snap.MinsZ = mins.Z;
                }

                if (maxs != null)
                {
                    snap.MaxsX = maxs.X;
                    snap.MaxsY = maxs.Y;
                    snap.MaxsZ = maxs.Z;
                }
            }

            _activeSlots[_activeSlotCount++] = slot;

            if (snap.IsAlive)
            {
                if (snap.Team == CsTeam.Terrorist)
                    _terroristSlots[_terroristSlotCount++] = slot;
                else if (snap.Team == CsTeam.CounterTerrorist)
                    _counterTerroristSlots[_counterTerroristSlotCount++] = slot;
            }

            _entityOwnershipResolver.RegisterHandle(pawn.EntityHandle.Raw, slot, currentTick);
            _entityOwnershipResolver.RegisterHandle(controller.EntityHandle.Raw, slot, currentTick);

            int entityIdx = 0;
            AddAssociatedEntity(slot, ref entityIdx, pawn, currentTick);
            CollectDirectAssociatedEntities(slot, ref entityIdx, pawn, currentTick);
            snap.AssociatedEntityCount = entityIdx;
        }

        RegisterCachedReverseLinkedMetadata(currentTick);
        UpdateReverseLinkedEntityCache(currentTick);
        AppendReverseLinkedEntitiesToSnapshots();
    }

    private float TryGetWeaponMaxSpeed(CCSPlayerPawn pawn)
    {
        const float DefaultMaxSpeed = 250.0f;

        try
        {
            var weaponServices = pawn.WeaponServices;
            var activeWeapon = weaponServices?.ActiveWeapon.Value;
            var weaponVData = activeWeapon?.As<CCSWeaponBase>()?.VData;
            var maxSpeed = weaponVData?.MaxSpeed;
            if (maxSpeed == null)
                return DefaultMaxSpeed;

            Span<float> values = maxSpeed.Values;
            float fastest = 0.0f;
            float slowestPositive = 0.0f;
            for (int i = 0; i < values.Length; i++)
            {
                float candidate = values[i];
                if (candidate <= 0.0f)
                    continue;

                if (candidate > fastest)
                    fastest = candidate;

                if (slowestPositive <= 0.0f || candidate < slowestPositive)
                    slowestPositive = candidate;
            }

            if (pawn.IsScoped && slowestPositive > 0.0f)
                return slowestPositive;

            return fastest > 0.0f ? fastest : DefaultMaxSpeed;
        }
        catch (Exception ex)
        {
            LogSuppressedException("weapon max speed", ex, pawn);
            return DefaultMaxSpeed;
        }
    }

    private void ResetActiveSlots()
    {
        for (int i = 0; i < _activeSlotCount; i++)
        {
            _snapshots[_activeSlots[i]] = default;
        }

        _activeSlotCount = 0;
    }

    private void CollectDirectAssociatedEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, int currentTick)
    {
        TryCollectWeaponEntities(slot, ref entityIdx, pawn, currentTick);
        TryCollectWearables(slot, ref entityIdx, pawn, currentTick);
        TryCollectActionTrackedWeapon(slot, ref entityIdx, pawn, currentTick);
        TryCollectEffectEntity(slot, ref entityIdx, pawn, currentTick);
        TryCollectChildSceneEntities(slot, ref entityIdx, pawn, currentTick);
    }

    private void TryCollectWeaponEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, int currentTick)
    {
        try
        {
            var weaponServices = pawn.WeaponServices;
            if (weaponServices == null)
                return;

            var weapons = weaponServices.MyWeapons;
            if (weapons != null)
            {
                int weaponCount = weapons.Count;
                for (int w = 0; w < weaponCount; w++)
                {
                    try
                    {
                        var weapon = weapons[w].Value;
                        if (weapon != null && weapon.IsValid)
                            AddAssociatedEntity(slot, ref entityIdx, weapon, currentTick);
                    }
                    catch (Exception ex)
                    {
                        LogSuppressedException("weapon collect isolated", ex, pawn);
                    }
                }
            }

            try
            {
                var activeWeapon = weaponServices.ActiveWeapon.Value;
                if (activeWeapon != null && activeWeapon.IsValid)
                    AddAssociatedEntityUnique(slot, ref entityIdx, activeWeapon, currentTick);
            }
            catch (Exception ex)
            {
                LogSuppressedException("active weapon collect isolated", ex, pawn);
            }

            try
            {
                var lastWeapon = weaponServices.LastWeapon.Value;
                if (lastWeapon != null && lastWeapon.IsValid)
                    AddAssociatedEntityUnique(slot, ref entityIdx, lastWeapon, currentTick);
            }
            catch (Exception ex)
            {
                LogSuppressedException("last weapon collect isolated", ex, pawn);
            }
        }
        catch (Exception ex)
        {
            LogSuppressedException("weapon collect", ex, pawn);
        }
    }

    private void TryCollectWearables(int slot, ref int entityIdx, CCSPlayerPawn pawn, int currentTick)
    {
        try
        {
            var wearables = pawn.MyWearables;
            if (wearables == null)
                return;

            int wearableCount = wearables.Count;
            for (int w = 0; w < wearableCount; w++)
            {
                try
                {
                    var wearable = wearables[w].Value;
                    if (wearable != null && wearable.IsValid)
                        AddAssociatedEntity(slot, ref entityIdx, wearable, currentTick);
                }
                catch (Exception ex)
                {
                    LogSuppressedException("wearable collect isolated", ex, pawn);
                }
            }
        }
        catch (Exception ex)
        {
            LogSuppressedException("wearable collect", ex, pawn);
        }
    }

    private void TryCollectActionTrackedWeapon(int slot, ref int entityIdx, CCSPlayerPawn pawn, int currentTick)
    {
        try
        {
            var actionTracking = pawn.ActionTrackingServices;
            if (actionTracking == null)
                return;

            var lastWeaponBeforeAutoSwitch = actionTracking.LastWeaponBeforeC4AutoSwitch.Value;
            if (lastWeaponBeforeAutoSwitch != null && lastWeaponBeforeAutoSwitch.IsValid)
                AddAssociatedEntityUnique(slot, ref entityIdx, lastWeaponBeforeAutoSwitch, currentTick);
        }
        catch (Exception ex)
        {
            LogSuppressedException("action-tracked weapon collect", ex, pawn);
        }
    }

    private void TryCollectEffectEntity(int slot, ref int entityIdx, CCSPlayerPawn pawn, int currentTick)
    {
        try
        {
            var effectEntity = pawn.EffectEntity.Value;
            if (effectEntity != null && effectEntity.IsValid)
                AddAssociatedEntityUnique(slot, ref entityIdx, effectEntity, currentTick);
        }
        catch (Exception ex)
        {
            LogSuppressedException("effect entity collect", ex, pawn);
        }
    }

    private void TryCollectChildSceneEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, int currentTick)
    {
        try
        {
            var sceneNode = pawn.CBodyComponent?.SceneNode;
            if (sceneNode != null)
            {
                _entityOwnershipResolver.RegisterSceneNodeOwner(sceneNode.Handle, slot, currentTick);
                _childSceneEntityCurrentTick = currentTick;
                _childSceneEntityWriteIndex = entityIdx;

                _sceneNodeTraverser.CollectChildSceneEntities(slot, sceneNode, (int)pawn.Index, _childSceneEntityCallback,
                    (msg) => LogSuppressedWarning(msg, $"slot={slot}", pawn));

                entityIdx = _childSceneEntityWriteIndex;
            }
        }
        catch (Exception ex)
        {
            LogSuppressedException("child scene collect", ex, pawn);
        }
    }

    private void RegisterCachedReverseLinkedMetadata(int currentTick)
    {
        _staleEntityCleanupScratch.Clear();

        for (int slot = 0; slot < _reverseLinkedEntityIndicesBySlot.Length; slot++)
        {
            var reverseLinked = _reverseLinkedEntityIndicesBySlot[slot];
            foreach (int entityIndex in reverseLinked)
            {
                var entity = Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
                if (entity == null || !entity.IsValid)
                {
                    _staleEntityCleanupScratch.Add(entityIndex);
                    continue;
                }

                RegisterTrackedEntityMetadata(slot, entity, currentTick);
            }
        }

        CleanupStaleTrackedEntities();
    }

    private void UpdateReverseLinkedEntityCache(int currentTick)
    {
        // Process dirty entities FIRST for faster pickup of newly spawned entities.
        // Previously these were processed after the full rescan step, which could
        // delay ownership resolution by up to EntityRescanIntervalTicks.
        if (_dirtyEntityIndices.Count > 0)
        {
            _dirtyEntityProcessingScratch.Clear();
            foreach (int entityIndex in _dirtyEntityIndices)
                _dirtyEntityProcessingScratch.Add(entityIndex);

            _dirtyEntityIndices.Clear();

            for (int i = 0; i < _dirtyEntityProcessingScratch.Count; i++)
                ProcessDirtyReverseLinkedEntity(_dirtyEntityProcessingScratch[i], currentTick);
        }

        // Attempt to resolve unresolved scene-linked entities every frame
        ResolvePendingSceneLinkedEntities(currentTick);

        if (ShouldRunFullReverseLinkRescan(currentTick))
            BeginFullReverseLinkRescan();

        if (_fullReverseLinkRescanInProgress)
            ProcessFullReverseLinkRescanStep(currentTick);
    }

    private bool ShouldRunFullReverseLinkRescan(int currentTick)
    {
        if (_fullReverseLinkRescanInProgress)
            return false;

        if (_lastReverseLinkFullRescanTick == int.MinValue)
            return true;

        int rescanTicks = _config.Performance.EntityRescanIntervalTicks;
        return rescanTicks > 0 && currentTick - _lastReverseLinkFullRescanTick >= rescanTicks;
    }

    private void BeginFullReverseLinkRescan()
    {
        _fullRescanSeenEntityIndices.Clear();
        _fullReverseLinkRescanCursor = EntitySystem.FirstActiveEntity;
        _fullReverseLinkRescanInProgress = true;
    }

    private void ProcessFullReverseLinkRescanStep(int currentTick)
    {
        int budget = Math.Max(MinReverseLinkRescanBudgetPerFrame, _config.Performance.EntityRescanBudgetPerFrame);
        for (int i = 0; i < budget && _fullReverseLinkRescanCursor != IntPtr.Zero; i++)
        {
            var identity = new CEntityIdentity(_fullReverseLinkRescanCursor);
            IntPtr nextIdentity = identity.Next?.Handle ?? IntPtr.Zero;
            _fullReverseLinkRescanCursor = nextIdentity;

            CEntityInstance? entityInstance;
            try
            {
                entityInstance = new PointerTo<CEntityInstance>(identity.Handle).Value;
            }
            catch (Exception ex)
            {
                _reverseLinkDereferenceExceptionCount++;
                LogSuppressedException("reverse-link full rescan dereference", ex, null);
                continue;
            }

            if (entityInstance == null || !entityInstance.IsValid || entityInstance.Index <= 0)
                continue;

            int entityIndex = (int)entityInstance.Index;
            _fullRescanSeenEntityIndices.Add(entityIndex);
            _trackedEntityIndices.Add(entityIndex);
            _dirtyEntityIndices.Add(entityIndex);
        }

        if (_fullReverseLinkRescanCursor != IntPtr.Zero)
            return;

        _staleEntityCleanupScratch.Clear();
        foreach (int entityIndex in _trackedEntityIndices)
        {
            if (!_fullRescanSeenEntityIndices.Contains(entityIndex))
                _staleEntityCleanupScratch.Add(entityIndex);
        }

        CleanupStaleTrackedEntities();
        _lastReverseLinkFullRescanTick = currentTick;
        _fullReverseLinkRescanInProgress = false;
    }

    private void ProcessDirtyReverseLinkedEntity(int entityIndex, int currentTick)
    {
        if (entityIndex <= 0)
            return;

        var entity = Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
        if (entity == null || !entity.IsValid)
        {
            RemoveTrackedEntity(entityIndex);
            return;
        }

        _trackedEntityIndices.Add(entityIndex);

        int previousOwnerSlot = _entityOwnerSlotCache.GetValueOrDefault(entityIndex, -1);
        int ownerSlot = _entityOwnershipResolver.FindLinkedPlayerSlot(entity, currentTick, out nint unresolvedParentHandle);
        if (previousOwnerSlot >= 0 && previousOwnerSlot != ownerSlot)
            RemoveReverseLinkedEntityFromSlot(previousOwnerSlot, entityIndex);

        _unresolvedParentByEntityIndex.Remove(entityIndex);

        if (ownerSlot >= 0)
        {
            _entityOwnerSlotCache[entityIndex] = ownerSlot;
            AddReverseLinkedEntityToSlot(ownerSlot, entityIndex);
            RegisterTrackedEntityMetadata(ownerSlot, entity, currentTick);
            return;
        }

        _entityOwnerSlotCache.Remove(entityIndex);
        if (unresolvedParentHandle != 0)
        {
            if (_unresolvedParentByEntityIndex.TryGetValue(entityIndex, out UnresolvedEntityInfo existingInfo))
            {
                _unresolvedParentByEntityIndex[entityIndex] = new UnresolvedEntityInfo
                {
                    ParentHandle = unresolvedParentHandle,
                    FirstSeenTick = existingInfo.FirstSeenTick
                };
            }
            else
            {
                _unresolvedParentByEntityIndex[entityIndex] = new UnresolvedEntityInfo
                {
                    ParentHandle = unresolvedParentHandle,
                    FirstSeenTick = currentTick
                };
            }
        }
    }

    private void ResolvePendingSceneLinkedEntities(int currentTick)
    {
        if (_unresolvedParentByEntityIndex.Count == 0)
            return;

        _dirtyEntityProcessingScratch.Clear();
        _staleEntityCleanupScratch.Clear();

        foreach ((int entityIndex, UnresolvedEntityInfo unresolvedInfo) in _unresolvedParentByEntityIndex)
        {
            if (!_entityOwnershipResolver.TryGetSceneNodeOwnerSlot(unresolvedInfo.ParentHandle, currentTick, out int slot))
                continue;

            var entity = Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
            if (entity == null || !entity.IsValid)
            {
                _staleEntityCleanupScratch.Add(entityIndex);
                _dirtyEntityProcessingScratch.Add(entityIndex);
                continue;
            }

            _entityOwnerSlotCache[entityIndex] = slot;
            AddReverseLinkedEntityToSlot(slot, entityIndex);
            RegisterTrackedEntityMetadata(slot, entity, currentTick);
            _dirtyEntityProcessingScratch.Add(entityIndex);
        }

        for (int i = 0; i < _dirtyEntityProcessingScratch.Count; i++)
            _unresolvedParentByEntityIndex.Remove(_dirtyEntityProcessingScratch[i]);

        CleanupStaleTrackedEntities();
    }

    private void AppendReverseLinkedEntitiesToSnapshots()
    {
        _staleEntityCleanupScratch.Clear();

        for (int i = 0; i < _activeSlotCount; i++)
        {
            int slot = _activeSlots[i];
            ref var snap = ref _snapshots[slot];
            int entityIdx = snap.AssociatedEntityCount;
            var reverseLinked = _reverseLinkedEntityIndicesBySlot[slot];
            foreach (int entityIndex in reverseLinked)
            {
                var entity = Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
                if (entity == null || !entity.IsValid)
                {
                    _staleEntityCleanupScratch.Add(entityIndex);
                    continue;
                }

                AddAssociatedEntityIndexUnique(slot, ref entityIdx, entityIndex);
            }

            snap.AssociatedEntityCount = entityIdx;
        }

        CleanupStaleTrackedEntities();
    }

    private void CleanupStaleTrackedEntities()
    {
        if (_staleEntityCleanupScratch.Count == 0)
            return;

        for (int i = 0; i < _staleEntityCleanupScratch.Count; i++)
            RemoveTrackedEntity(_staleEntityCleanupScratch[i]);

        _staleEntityCleanupScratch.Clear();
    }

    private void AddChildSceneEntity(int slot, CBaseEntity entity)
    {
        AddAssociatedEntityUnique(slot, ref _childSceneEntityWriteIndex, entity, _childSceneEntityCurrentTick);
    }

    private bool AddAssociatedEntity(int slot, ref int entityIdx, CBaseEntity entity, int currentTick)
    {
        if (entityIdx >= PlayerSnapshot.MaxAssociatedEntities || !entity.IsValid || entity.Index <= 0)
            return false;

        StoreAssociatedEntityMetadata(slot, entityIdx, entity, currentTick);
        entityIdx++;
        return true;
    }

    private bool AddAssociatedEntityUnique(int slot, ref int entityIdx, CBaseEntity entity, int currentTick)
    {
        if (entityIdx >= PlayerSnapshot.MaxAssociatedEntities || !entity.IsValid || entity.Index <= 0)
            return false;

        return AddAssociatedEntityIndexUnique(slot, ref entityIdx, (int)entity.Index, entity, currentTick);
    }

    private bool AddAssociatedEntityIndexUnique(int slot, ref int entityIdx, int entityIndex, CBaseEntity? entity = null, int currentTick = 0)
    {
        if (entityIdx >= PlayerSnapshot.MaxAssociatedEntities || entityIndex <= 0)
            return false;

        for (int i = 0; i < entityIdx; i++)
        {
            if (_associatedEntities[slot, i] == entityIndex)
                return false;
        }

        if (entity != null)
            StoreAssociatedEntityMetadata(slot, entityIdx, entity, currentTick);
        else
            _associatedEntities[slot, entityIdx] = entityIndex;

        entityIdx++;
        return true;
    }

    private void StoreAssociatedEntityMetadata(int slot, int writeIndex, CBaseEntity entity, int currentTick)
    {
        _associatedEntities[slot, writeIndex] = (int)entity.Index;
        RegisterTrackedEntityMetadata(slot, entity, currentTick);

        // Only items that can legitimately become dropped world loot should retain
        // temporal ownership. Keeping cosmetic / pawn-linked entities here can
        // cause dead-player visuals to reappear when a nearby dropped weapon is revealed.
        if (ShouldTrackTemporalOwnership(entity))
            _entityOwnershipResolver.RecordTemporalOwnership((int)entity.Index, slot, currentTick);
    }

    private static bool ShouldTrackTemporalOwnership(CBaseEntity entity)
    {
        return entity is CBasePlayerWeapon;
    }

    private void RegisterTrackedEntityMetadata(int slot, CBaseEntity entity, int currentTick)
    {
        _entityOwnershipResolver.RegisterHandle(entity.EntityHandle.Raw, slot, currentTick);
        _entityOwnershipResolver.RegisterSceneNodeOwner(entity.CBodyComponent?.SceneNode?.Handle ?? 0, slot, currentTick);
    }

    private void AddReverseLinkedEntityToSlot(int slot, int entityIndex)
    {
        if ((uint)slot >= FowConstants.MaxSlots || entityIndex <= 0)
            return;

        _reverseLinkedEntityIndicesBySlot[slot].Add(entityIndex);
    }

    private void RemoveReverseLinkedEntityFromSlot(int slot, int entityIndex)
    {
        if ((uint)slot >= FowConstants.MaxSlots || entityIndex <= 0)
            return;

        _reverseLinkedEntityIndicesBySlot[slot].Remove(entityIndex);
    }

    private void RemoveTrackedEntity(int entityIndex)
    {
        _dirtyEntityIndices.Remove(entityIndex);
        _trackedEntityIndices.Remove(entityIndex);
        _unresolvedParentByEntityIndex.Remove(entityIndex);

        if (_entityOwnerSlotCache.Remove(entityIndex, out int slot))
            RemoveReverseLinkedEntityFromSlot(slot, entityIndex);
    }

    private void LogSuppressedException(string context, Exception ex, CEntityInstance? entity)
    {
        LogSuppressedWarning(context, ex.Message, entity);
    }

    private void LogSuppressedWarning(string context, string detail, CEntityInstance? entity)
    {
        _suppressedWarningCount++;
        string designerName = entity?.DesignerName ?? "unknown";
        string key = $"{context}:{designerName}";
        int currentTick = Server.TickCount;
        if (_nextWarningTicksByKey.TryGetValue(key, out int nextTick) && currentTick < nextTick)
            return;

        _nextWarningTicksByKey[key] = currentTick + WarningIntervalTicks;

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(PluginOutput.Prefix($"Warning: {context} on {designerName} | {detail}"));
        Console.ResetColor();
    }
}
