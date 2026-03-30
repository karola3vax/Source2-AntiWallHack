using System;
using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Util;

namespace S2FOW.Core;

/// <summary>
/// Tracks bullet impact-like entities and associates them back to the shooter.
/// OwnerEntity is preferred when available; otherwise recent bullet impact
/// positions are used as a fallback.
/// </summary>
public class ImpactTracker
{
    private const int MaxImpactsPerPlayer = 8;
    private const int ImpactRetentionTicks = 64; // ~1 second at 64 tick
    private const float ImpactAssociationDistanceSqr = 96.0f * 96.0f;

    private struct ImpactSample
    {
        public float X;
        public float Y;
        public float Z;
        public int ExpiryTick;
    }

    private readonly ImpactSample[,] _impactSamples = new ImpactSample[FowConstants.MaxSlots, MaxImpactsPerPlayer];
    private readonly int[] _impactWriteIndex = new int[FowConstants.MaxSlots];
    private readonly Dictionary<int, int> _impactEntityToSlot = new(64);
    private long _ownerResolveFailureCount;

    public long OwnerResolveFailureCount => _ownerResolveFailureCount;

    /// <summary>
    /// Records a recent bullet impact position for the shooter.
    /// </summary>
    public void OnBulletImpact(int shooterSlot, float x, float y, float z, int currentTick)
    {
        if (!FowConstants.IsValidSlot(shooterSlot))
            return;

        int writeIndex = _impactWriteIndex[shooterSlot] % MaxImpactsPerPlayer;
        _impactSamples[shooterSlot, writeIndex] = new ImpactSample
        {
            X = x,
            Y = y,
            Z = z,
            ExpiryTick = currentTick + ImpactRetentionTicks
        };
        _impactWriteIndex[shooterSlot]++;
    }

    /// <summary>
    /// Handles entity creation for impact-like entities.
    /// </summary>
    public void OnEntityCreated(CEntityInstance entity)
    {
        TryTrackImpactEntity(entity);
    }

    /// <summary>
    /// Handles entity spawn for impact-like entities.
    /// </summary>
    public void OnEntitySpawned(CEntityInstance entity)
    {
        TryTrackImpactEntity(entity);
    }

    /// <summary>
    /// Removes tracking data for a deleted impact entity.
    /// </summary>
    public void OnEntityDeleted(CEntityInstance entity)
    {
        if (entity == null)
            return;

        int entityIndex = (int)entity.Index;
        if (entityIndex > 0)
            _impactEntityToSlot.Remove(entityIndex);
    }

    /// <summary>
    /// Returns the active impact-to-shooter mapping for visibility checks.
    /// </summary>
    public Dictionary<int, int>.Enumerator GetActiveImpactEntities()
    {
        return _impactEntityToSlot.GetEnumerator();
    }

    /// <summary>
    /// Clears all tracked impact state.
    /// </summary>
    public void Clear()
    {
        _impactEntityToSlot.Clear();
        Array.Clear(_impactWriteIndex);
        Array.Clear(_impactSamples);
    }

    public int ActiveCount => _impactEntityToSlot.Count;

    private static bool IsImpactEntityType(string designerName)
    {
        return designerName.StartsWith("decal", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("impact", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("blood", StringComparison.OrdinalIgnoreCase) ||
               designerName == "env_blood" ||
               designerName == "env_decal";
    }

    private void TryTrackImpactEntity(CEntityInstance entity)
    {
        if (entity == null || !entity.IsValid)
            return;

        string? designerName = entity.DesignerName;
        if (string.IsNullOrEmpty(designerName) || !IsImpactEntityType(designerName))
            return;

        int entityIndex = (int)entity.Index;
        if (entityIndex <= 0 || _impactEntityToSlot.ContainsKey(entityIndex))
            return;

        int ownerSlot = ResolveImpactOwner(entity, Server.TickCount);
        if (FowConstants.IsValidSlot(ownerSlot))
            _impactEntityToSlot[entityIndex] = ownerSlot;
    }

    private int ResolveImpactOwner(CEntityInstance entity, int currentTick)
    {
        try
        {
            if (entity is CBaseEntity baseEntity)
            {
                var owner = baseEntity.OwnerEntity.Value;
                if (owner is CCSPlayerPawn pawn)
                {
                    var controller = pawn.Controller.Value;
                    if (controller is CCSPlayerController playerController && FowConstants.IsValidSlot(playerController.Slot))
                        return playerController.Slot;
                }
            }
        }
        catch
        {
            _ownerResolveFailureCount++;
            // Ignore transient creation-time entity failures.
        }

        return ResolveImpactOwnerFromRecentPosition(entity, currentTick);
    }

    private int ResolveImpactOwnerFromRecentPosition(CEntityInstance entity, int currentTick)
    {
        if (entity is not CBaseEntity baseEntity)
            return -1;

        var absOrigin = baseEntity.AbsOrigin;
        if (absOrigin == null)
            return -1;

        float bestDistanceSqr = ImpactAssociationDistanceSqr;
        int bestSlot = -1;

        for (int slot = 0; slot < FowConstants.MaxSlots; slot++)
        {
            for (int i = 0; i < MaxImpactsPerPlayer; i++)
            {
                ImpactSample sample = _impactSamples[slot, i];
                if (sample.ExpiryTick <= currentTick)
                    continue;

                float distanceSqr = VectorMath.DistanceSquared(
                    sample.X, sample.Y, sample.Z,
                    absOrigin.X, absOrigin.Y, absOrigin.Z);
                if (distanceSqr <= bestDistanceSqr)
                {
                    bestDistanceSqr = distanceSqr;
                    bestSlot = slot;
                }
            }
        }

        return bestSlot;
    }
}
