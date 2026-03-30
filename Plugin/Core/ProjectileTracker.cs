using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Core;

namespace S2FOW.Core;

/// <summary>
/// Tracks in-flight projectile entities (grenades, breach charges, bump mines, etc.)
/// and maps them to their owning player slot. This allows CheckTransmit to hide
/// projectiles thrown by hidden players, preventing trajectory-based wallhack information leaks.
/// </summary>
public class ProjectileTracker
{
    private const int MaxTrackedProjectiles = 64;

    // Designer names for projectile types that can leak thrower information.
    private static readonly HashSet<string> ProjectileDesignerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hegrenade_projectile",
        "flashbang_projectile",
        "smokegrenade_projectile",
        "molotov_projectile",
        "decoy_projectile",
        "breachcharge_projectile",
        "bumpmine_projectile",
        "tripwirefire_projectile"
    };

    // Entity index to owning player slot.
    private readonly Dictionary<int, int> _projectileOwnerSlot = new(MaxTrackedProjectiles);

    // Entity index to cached world position for proximity checks.
    private readonly Dictionary<int, (float X, float Y, float Z)> _projectilePositions = new(MaxTrackedProjectiles);
    private long _entityAccessFailureCount;
    private long _ownerResolveFailureCount;

    public long EntityAccessFailureCount => _entityAccessFailureCount;
    public long OwnerResolveFailureCount => _ownerResolveFailureCount;

    /// <summary>
    /// Handles entity creation and caches the owner when the entity is a tracked projectile.
    /// </summary>
    public void OnEntityCreated(CEntityInstance entity)
    {
        if (entity == null || !entity.IsValid)
            return;

        string? designerName = entity.DesignerName;
        if (string.IsNullOrEmpty(designerName) || !ProjectileDesignerNames.Contains(designerName))
            return;

        int entityIndex = (int)entity.Index;
        if (entityIndex <= 0)
            return;

        // Resolve the owner through Thrower first, then fall back to OwnerEntity.
        int ownerSlot = ResolveProjectileOwner(entity);
        if (ownerSlot >= 0 && FowConstants.IsValidSlot(ownerSlot))
        {
            _projectileOwnerSlot[entityIndex] = ownerSlot;
        }
    }

    /// <summary>
    /// Handles entity spawn and retries owner resolution when creation happened too early.
    /// </summary>
    public void OnEntitySpawned(CEntityInstance entity)
    {
        if (entity == null || !entity.IsValid)
            return;

        int entityIndex = (int)entity.Index;
        if (entityIndex <= 0)
            return;

        // Skip projectiles that were already resolved during creation.
        if (_projectileOwnerSlot.ContainsKey(entityIndex))
            return;

        string? designerName = entity.DesignerName;
        if (string.IsNullOrEmpty(designerName) || !ProjectileDesignerNames.Contains(designerName))
            return;

        int ownerSlot = ResolveProjectileOwner(entity);
        if (ownerSlot >= 0 && FowConstants.IsValidSlot(ownerSlot))
        {
            _projectileOwnerSlot[entityIndex] = ownerSlot;
        }
    }

    /// <summary>
    /// Removes tracking data for a deleted entity.
    /// </summary>
    public void OnEntityDeleted(CEntityInstance entity)
    {
        if (entity == null)
            return;

        int entityIndex = (int)entity.Index;
        if (entityIndex <= 0)
            return;

        _projectileOwnerSlot.Remove(entityIndex);
        _projectilePositions.Remove(entityIndex);
    }

    /// <summary>
    /// Returns the active projectile-to-owner mapping for visibility checks.
    /// </summary>
    public Dictionary<int, int>.Enumerator GetActiveProjectiles()
    {
        return _projectileOwnerSlot.GetEnumerator();
    }

    /// <summary>
    /// Returns the cached projectile position used by proximity checks.
    /// </summary>
    public bool TryGetProjectilePosition(int entityIndex, out float x, out float y, out float z)
    {
        if (_projectilePositions.TryGetValue(entityIndex, out var pos))
        {
            x = pos.X;
            y = pos.Y;
            z = pos.Z;
            return true;
        }
        x = y = z = 0f;
        return false;
    }

    /// <summary>
    /// Refreshes cached positions for all tracked projectiles.
    /// Call once per frame before CheckTransmit processing.
    /// </summary>
    public void UpdatePositions()
    {
        // Copy keys out first so the dictionaries can be cleaned up safely during iteration.
        Span<int> indices = _projectileOwnerSlot.Count <= 64
            ? stackalloc int[_projectileOwnerSlot.Count]
            : new int[_projectileOwnerSlot.Count];

        int count = 0;
        foreach (var kvp in _projectileOwnerSlot)
        {
            indices[count++] = kvp.Key;
        }

        for (int i = 0; i < count; i++)
        {
            int entityIndex = indices[i];
            try
            {
                var entity = CounterStrikeSharp.API.Utilities.GetEntityFromIndex<CBaseEntity>(entityIndex);
                if (entity != null && entity.IsValid && entity.AbsOrigin != null)
                {
                    _projectilePositions[entityIndex] = (
                        entity.AbsOrigin.X,
                        entity.AbsOrigin.Y,
                        entity.AbsOrigin.Z
                    );
                }
                else
                {
                    // The entity is no longer valid, so drop the cached entry.
                    _projectileOwnerSlot.Remove(entityIndex);
                    _projectilePositions.Remove(entityIndex);
                }
            }
            catch
            {
                _entityAccessFailureCount++;
                // If entity access fails, drop the cached entry.
                _projectileOwnerSlot.Remove(entityIndex);
                _projectilePositions.Remove(entityIndex);
            }
        }
    }

    /// <summary>
    /// Clears all tracked projectiles.
    /// </summary>
    public void Clear()
    {
        _projectileOwnerSlot.Clear();
        _projectilePositions.Clear();
    }

    public int ActiveCount => _projectileOwnerSlot.Count;

    private int ResolveProjectileOwner(CEntityInstance entity)
    {
        try
        {
            // Try CBaseGrenade.Thrower first because it is the most reliable source.
            if (entity is CBaseGrenade grenade)
            {
                var thrower = grenade.Thrower.Value;
                if (thrower != null && thrower.IsValid && thrower is CCSPlayerPawn pawn)
                {
                    var controller = pawn.Controller.Value;
                    if (controller != null && controller.IsValid && controller is CCSPlayerController playerController)
                        return playerController.Slot;
                }
            }

            // Fall back to OwnerEntity when Thrower is unavailable.
            if (entity is CBaseEntity baseEntity)
            {
                var owner = baseEntity.OwnerEntity.Value;
                if (owner != null && owner.IsValid)
                {
                    // Owner may be the pawn directly.
                    if (owner is CCSPlayerPawn ownerPawn)
                    {
                        var controller = ownerPawn.Controller.Value;
                        if (controller != null && controller.IsValid && controller is CCSPlayerController playerController)
                            return playerController.Slot;
                    }
                    // Owner may already be a controller.
                    if (owner is CCSPlayerController directController)
                        return directController.Slot;
                }
            }
        }
        catch
        {
            _ownerResolveFailureCount++;
            // Ignore transient creation-time entity failures.
        }

        return -1;
    }
}
