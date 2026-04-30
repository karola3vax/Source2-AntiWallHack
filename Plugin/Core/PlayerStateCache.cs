using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

/// <summary>
/// Reads and stores one frame of player state.
///
/// S2FOW needs each player's position, view direction, team, weapon, body size,
/// and connected objects. Reading that data once per frame keeps all visibility
/// decisions consistent and avoids repeatedly walking live engine objects.
///
/// This class also records every networked object that must be hidden together
/// with a player body: weapons, wearables, hostage carry objects, and attached
/// scene objects. If S2FOW cannot collect that full list, the player stays visible
/// because hiding only part of a player can crash the client.
/// </summary>
public class PlayerStateCache
{
    /// <summary>
    /// How many ticks to wait before logging the same warning again.
    /// 128 ticks × 0.015625 s/tick = 2.0 seconds at 64 Hz.
    /// Prevents the console from being flooded with repeated errors
    /// (e.g., a broken weapon entity that fails every frame).
    /// </summary>
    private const int WarningIntervalTicks = 128;

    /// <summary>Maximum attached scene objects scanned from any one connected object.</summary>
    private const int MaxSceneNodesPerRoot = 96;

    /// <summary>Maximum depth followed in the attached-object tree.</summary>
    private const int MaxSceneNodeDepth = 8;

    /// <summary>Maximum sibling objects followed at each attached-object level.</summary>
    private const int MaxSceneNodeSiblingsPerLevel = 96;

    // ────────────────────────────────────────────────────────────────────────
    //  Storage
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>One snapshot per possible player slot (0–63).</summary>
    private readonly PlayerSnapshot[] _snapshots = new PlayerSnapshot[FowConstants.MaxSlots];

    /// <summary>
    /// 2D array of connected object indexes for each player.
    /// For slot 5 with 7 objects: _associatedEntities[5, 0] through [5, 6].
    /// These include the body, weapons, wearables, hostage objects, and attached
    /// scene objects that must be hidden with the body.
    /// </summary>
    private readonly int[,] _associatedEntities = new int[FowConstants.MaxSlots, PlayerSnapshot.MaxAssociatedEntities];

    /// <summary>List of slot numbers that have valid snapshots this frame.</summary>
    private readonly int[] _activeSlots = new int[FowConstants.MaxSlots];

    /// <summary>Tracks when each warning type was last logged, to avoid spam.</summary>
    private readonly Dictionary<string, int> _nextWarningTicksByKey = new();

    /// <summary>How many active player slots are in the _activeSlots array.</summary>
    private int _activeSlotCount;

    /// <summary>How many warnings have been suppressed (rate-limited) in total.</summary>
    private long _suppressedWarningCount;

    /// <summary>How many player reads could not collect every connected object.</summary>
    private long _dependentEntityCollectionFailureCount;

    /// <summary>How many player reads found more connected objects than S2FOW can store.</summary>
    private long _associatedEntityOverflowCount;

    /// <summary>Total attached scene objects added to player connected-object lists.</summary>
    private long _sceneChildEntitiesCollected;

    // ────────────────────────────────────────────────────────────────────────
    //  Public API
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Read-only view of all player snapshots. Index by slot number (0–63).</summary>
    public ReadOnlySpan<PlayerSnapshot> Snapshots => _snapshots;

    /// <summary>Read-only list of slot numbers that have valid players this frame.</summary>
    public ReadOnlySpan<int> ActiveSlots => _activeSlots.AsSpan(0, _activeSlotCount);

    /// <summary>How many warnings have been suppressed since the last reset.</summary>
    public long SuppressedWarningCount => _suppressedWarningCount;

    /// <summary>How many player reads were unsafe because connected objects could not be fully read.</summary>
    public long DependentEntityCollectionFailureCount => _dependentEntityCollectionFailureCount;

    /// <summary>How many player reads found more connected objects than the fixed list can hold.</summary>
    public long AssociatedEntityOverflowCount => _associatedEntityOverflowCount;

    /// <summary>Total attached scene objects added to connected-object lists since the last reset.</summary>
    public long SceneChildEntitiesCollected => _sceneChildEntitiesCollected;

    /// <summary>Gets the engine object index of the Nth connected object for a player slot.</summary>
    public int GetAssociatedEntity(int slot, int index)
    {
        return _associatedEntities[slot, index];
    }

    /// <summary>Clears warning tracking. Called on map changes.</summary>
    public void ResetTracking()
    {
        _nextWarningTicksByKey.Clear();
        _suppressedWarningCount = 0;
        _dependentEntityCollectionFailureCount = 0;
        _associatedEntityOverflowCount = 0;
        _sceneChildEntitiesCollected = 0;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Snapshot building (called once per frame)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures a fresh read of every connected player's state.
    ///
    /// For each player, we read:
    ///   - Position (feet and eyes), view angles, velocity.
    ///   - Team, alive/dead status, bot flag.
    ///   - Collision bounds (bounding box).
    ///   - Active weapon type and max movement speed.
    ///   - All connected object indexes that must be hidden with the player body.
    /// </summary>
    public void BuildSnapshots(List<CCSPlayerController> players)
    {
        // Clear previous frame's data.
        ResetActiveSlots();

        for (int i = 0; i < players.Count; i++)
        {
            int slot = -1;
            try
            {
            var controller = players[i];
            if (!controller.IsValid)
                continue;

            slot = controller.Slot;
            if (!FowConstants.IsValidSlot(slot))
                continue;

            // The pawn is the player's physical body in the world.
            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var absOrigin = pawn.AbsOrigin;
            if (absOrigin == null)
                continue;

            // Fill in the snapshot for this slot.
            ref var snap = ref _snapshots[slot];
            snap.Slot = slot;
            snap.IsValid = true;
            snap.IsAlive = pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE;
            snap.Team = controller.Team;
            snap.IsBot = controller.IsBot;
            snap.PawnEntityIndex = pawn.Index;
            snap.HasValidPawnController = HasValidPawnController(pawn, ref snap);
            snap.PosX = absOrigin.X;
            snap.PosY = absOrigin.Y;
            snap.PosZ = absOrigin.Z;

            // Eye position = feet position + view offset (height of the eyes above the feet).
            var viewOffset = pawn.ViewOffset;
            snap.ViewOffsetZ = viewOffset.Z;
            snap.EyePosX = absOrigin.X + viewOffset.X;
            snap.EyePosY = absOrigin.Y + viewOffset.Y;
            snap.EyePosZ = absOrigin.Z + viewOffset.Z;

            // View angles (where the player is looking).
            var eyeAngles = pawn.EyeAngles;
            snap.Pitch = eyeAngles?.X ?? 0.0f;
            snap.Yaw = eyeAngles?.Y ?? pawn.AbsRotation?.Y ?? 0.0f;

            // Velocity (speed and direction of movement).
            var vel = pawn.AbsVelocity;
            if (vel != null)
            {
                snap.VelX = vel.X;
                snap.VelY = vel.Y;
                snap.VelZ = vel.Z;
            }

            // Weapon data affects movement speed and which weapon-tip body points apply.
            snap.WeaponMaxSpeed = TryGetWeaponMaxSpeed(pawn);
            snap.ActiveWeaponLosClass = TryGetActiveWeaponLosClass(pawn);

            // Is the player on the ground? (Affects vertical prediction.)
            snap.IsOnGround = (pawn.Flags & (uint)Flags_t.FL_ONGROUND) != 0;

            // Collision bounds (the invisible box around the player used for physics).
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

            // Register this slot as active for iteration.
            _activeSlots[_activeSlotCount++] = slot;

            // Collect every object that must be hidden with this player body.
            // This includes the body, inventory weapons, current/previous weapon,
            // wearables, hostage carry objects, and attached scene objects.
            // If the body is hidden but one connected object is still sent, the
            // client can crash because that object refers to a body it never received.
            int entityIdx = 0;
            AddAssociatedEntity(slot, ref entityIdx, pawn, ref snap);
            CollectWeaponEntities(slot, ref entityIdx, pawn, ref snap);
            CollectWearableEntities(slot, ref entityIdx, pawn, ref snap);
            CollectHostageEntities(slot, ref entityIdx, pawn, ref snap);
            CollectSceneNodeClosure(slot, ref entityIdx, pawn, ref snap);
            snap.AssociatedEntityCount = entityIdx;
            }
            catch (Exception ex)
            {
                if (FowConstants.IsValidSlot(slot))
                    _snapshots[slot] = default;

                if (_activeSlotCount > 0 && _activeSlots[_activeSlotCount - 1] == slot)
                    _activeSlotCount--;

                LogSuppressedException("player snapshot", ex, null);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Weapon data extraction
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the maximum movement speed allowed by the player's active weapon.
    /// Different weapons have different speed caps (e.g., knife = 250, AWP = 200).
    /// If the player is scoped, returns the slowest speed mode (scoped AWP = 100).
    /// Returns 250 (default) if the weapon data cannot be read.
    /// </summary>
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

            // Some weapons have multiple speed values (unscoped vs scoped).
            // Find both the fastest and slowest positive values.
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

            // Scoped players use the slower speed mode.
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

    /// <summary>
    /// Determines the weapon class (Pistol, Rifle, Sniper) of the player's active weapon.
    /// This affects which body check points are used for visibility testing —
    /// a sniper rifle extends further from the body than a pistol.
    /// </summary>
    private WeaponLosClass TryGetActiveWeaponLosClass(CCSPlayerPawn pawn)
    {
        try
        {
            var activeWeapon = pawn.WeaponServices?.ActiveWeapon.Value;
            var weaponBase = activeWeapon?.As<CCSWeaponBase>();
            var weaponVData = weaponBase?.VData;
            if (weaponVData == null)
                return WeaponLosClass.None;

            return weaponVData.WeaponType switch
            {
                CSWeaponType.WEAPONTYPE_PISTOL => WeaponLosClass.Pistol,
                CSWeaponType.WEAPONTYPE_RIFLE => WeaponLosClass.Rifle,
                CSWeaponType.WEAPONTYPE_SNIPER_RIFLE => WeaponLosClass.Sniper,
                _ => WeaponLosClass.None
            };
        }
        catch (Exception ex)
        {
            LogSuppressedException("weapon los class", ex, pawn);
            return WeaponLosClass.None;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Internal helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Clears previous frame's snapshots and resets the active slot count.</summary>
    private void ResetActiveSlots()
    {
        for (int i = 0; i < _activeSlotCount; i++)
            _snapshots[_activeSlots[i]] = default;

        _activeSlotCount = 0;
    }

    /// <summary>
    /// Collects all weapon object indexes for a player.
    /// Weapons are separate networked objects. If the player body is hidden,
    /// these weapons must be hidden from the same viewer too.
    /// </summary>
    private void CollectWeaponEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, ref PlayerSnapshot snap)
    {
        try
        {
            var weaponServices = pawn.WeaponServices;
            if (weaponServices == null)
                return;

            // All weapons in the player's inventory.
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
                        {
                            bool added = AddAssociatedEntityUnique(slot, ref entityIdx, weapon, ref snap);
                            if (added)
                                CollectSceneNodeClosure(slot, ref entityIdx, weapon, ref snap);
                        }
                    }
                    catch (Exception ex)
                    {
                        MarkDependentEntityCollectionFailed(ref snap);
                        LogSuppressedException("weapon collect isolated", ex, pawn);
                    }
                }
            }

            // The currently held weapon (may already be in the list, deduped by AddUnique).
            try
            {
                var activeWeapon = weaponServices.ActiveWeapon.Value;
                if (activeWeapon != null && activeWeapon.IsValid)
                {
                    bool added = AddAssociatedEntityUnique(slot, ref entityIdx, activeWeapon, ref snap);
                    if (added)
                        CollectSceneNodeClosure(slot, ref entityIdx, activeWeapon, ref snap);
                }
            }
            catch (Exception ex)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                LogSuppressedException("active weapon collect isolated", ex, pawn);
            }

            // The previously held weapon (for weapon-switch animations).
            try
            {
                var lastWeapon = weaponServices.LastWeapon.Value;
                if (lastWeapon != null && lastWeapon.IsValid)
                {
                    bool added = AddAssociatedEntityUnique(slot, ref entityIdx, lastWeapon, ref snap);
                    if (added)
                        CollectSceneNodeClosure(slot, ref entityIdx, lastWeapon, ref snap);
                }
            }
            catch (Exception ex)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                LogSuppressedException("last weapon collect isolated", ex, pawn);
            }
        }
        catch (Exception ex)
        {
            MarkDependentEntityCollectionFailed(ref snap);
            LogSuppressedException("weapon collect", ex, pawn);
        }
    }

    /// <summary>
    /// Collects all wearable object indexes for a player.
    ///
    /// Gloves, agent accessories, and similar wearables are separate networked
    /// objects. If the body is hidden but a wearable is still sent, the client can
    /// crash because the wearable points at a body the client does not have.
    /// </summary>
    private void CollectWearableEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, ref PlayerSnapshot snap)
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
                    {
                        bool added = AddAssociatedEntityUnique(slot, ref entityIdx, wearable, ref snap);
                        if (added)
                            CollectSceneNodeClosure(slot, ref entityIdx, wearable, ref snap);
                    }
                }
                catch (Exception ex)
                {
                    MarkDependentEntityCollectionFailed(ref snap);
                    LogSuppressedException("wearable collect isolated", ex, pawn);
                }
            }
        }
        catch (Exception ex)
        {
            MarkDependentEntityCollectionFailed(ref snap);
            LogSuppressedException("wearable collect", ex, pawn);
        }
    }

    /// <summary>
    /// Collects hostage-related objects for a player.
    ///
    /// On hostage maps, a carried hostage uses separate networked objects attached
    /// to the player body. Those objects must be hidden together with the body.
    /// </summary>
    private void CollectHostageEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, ref PlayerSnapshot snap)
    {
        try
        {
            var hostageServices = pawn.HostageServices;
            if (hostageServices == null)
                return;

            // The hostage carry prop is a visual model attached to the player's back.
            try
            {
                var carriedHostage = hostageServices.CarriedHostage.Value;
                if (carriedHostage != null && carriedHostage.IsValid)
                {
                    bool added = AddAssociatedEntityUnique(slot, ref entityIdx, carriedHostage, ref snap);
                    if (added)
                        CollectSceneNodeClosure(slot, ref entityIdx, carriedHostage, ref snap);
                }
            }
            catch (Exception ex)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                LogSuppressedException("hostage entity collect isolated", ex, pawn);
            }

            try
            {
                var carriableProp = hostageServices.CarriedHostageProp.Value;
                if (carriableProp != null && carriableProp.IsValid)
                {
                    bool added = AddAssociatedEntityUnique(slot, ref entityIdx, carriableProp, ref snap);
                    if (added)
                        CollectSceneNodeClosure(slot, ref entityIdx, carriableProp, ref snap);
                }
            }
            catch (Exception ex)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                LogSuppressedException("hostage prop collect isolated", ex, pawn);
            }
        }
        catch (Exception ex)
        {
            MarkDependentEntityCollectionFailed(ref snap);
            LogSuppressedException("hostage collect", ex, pawn);
        }
    }

    /// <summary>Returns true only when the player body still has a valid engine controller.</summary>
    private bool HasValidPawnController(CCSPlayerPawn pawn, ref PlayerSnapshot snap)
    {
        try
        {
            var controllerHandle = pawn.Controller;
            var controller = controllerHandle.Value;
            return controllerHandle.IsValid && controller != null && controller.IsValid;
        }
        catch (Exception ex)
        {
            MarkDependentEntityCollectionFailed(ref snap);
            LogSuppressedException("pawn controller validate", ex, pawn);
            return false;
        }
    }

    /// <summary>Adds one connected object index to a player's list.</summary>
    private bool AddAssociatedEntity(int slot, ref int entityIdx, CBaseEntity entity, ref PlayerSnapshot snap)
    {
        if (entity == null || !entity.IsValid || !FowConstants.IsValidEntityIndex((int)entity.Index))
            return false;

        if (entityIdx >= PlayerSnapshot.MaxAssociatedEntities)
        {
            MarkAssociatedEntityOverflow(ref snap);
            return false;
        }

        _associatedEntities[slot, entityIdx] = (int)entity.Index;
        entityIdx++;
        return true;
    }

    /// <summary>
    /// Same as AddAssociatedEntity, but skips duplicates. A weapon can appear in
    /// multiple engine lists, so S2FOW records it only once.
    /// </summary>
    private bool AddAssociatedEntityUnique(int slot, ref int entityIdx, CBaseEntity entity, ref PlayerSnapshot snap)
    {
        if (entity == null || !entity.IsValid || !FowConstants.IsValidEntityIndex((int)entity.Index))
            return false;

        int entityIndex = (int)entity.Index;
        for (int i = 0; i < entityIdx; i++)
        {
            if (_associatedEntities[slot, i] == entityIndex)
                return false; // Already in the list.
        }

        if (entityIdx >= PlayerSnapshot.MaxAssociatedEntities)
        {
            MarkAssociatedEntityOverflow(ref snap);
            return false;
        }

        _associatedEntities[slot, entityIdx] = entityIndex;
        entityIdx++;
        return true;
    }

    /// <summary>
    /// Adds valid attached scene objects reachable through the engine's scene tree.
    /// These are common visuals attached to a player body and must be hidden with it.
    /// </summary>
    private void CollectSceneNodeClosure(int slot, ref int entityIdx, CBaseEntity entity, ref PlayerSnapshot snap)
    {
        try
        {
            var sceneNode = entity.CBodyComponent?.SceneNode;
            if (sceneNode == null)
                return;

            int scannedNodes = 0;
            CollectSceneNodeParentOwners(slot, ref entityIdx, sceneNode.PParent, ref snap);
            CollectSceneNodeChildren(slot, ref entityIdx, sceneNode.Child, ref snap, depth: 0, ref scannedNodes);
        }
        catch (Exception ex)
        {
            MarkDependentEntityCollectionFailed(ref snap);
            LogSuppressedException("scene-node closure collect", ex, entity);
        }
    }

    private void CollectSceneNodeChildren(
        int slot,
        ref int entityIdx,
        CGameSceneNode? node,
        ref PlayerSnapshot snap,
        int depth,
        ref int scannedNodes)
    {
        if (node == null)
            return;

        if (depth > MaxSceneNodeDepth)
        {
            MarkDependentEntityCollectionFailed(ref snap);
            return;
        }

        int siblingCount = 0;
        while (node != null)
        {
            if (siblingCount >= MaxSceneNodeSiblingsPerLevel || scannedNodes >= MaxSceneNodesPerRoot)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                return;
            }

            siblingCount++;
            scannedNodes++;

            try
            {
                CBaseEntity? owner = TryGetSceneNodeOwnerEntity(node);
                if (owner != null && owner.IsValid)
                {
                    bool added = AddAssociatedEntityUnique(slot, ref entityIdx, owner, ref snap);
                    if (added)
                    {
                        snap.SceneChildEntityCount++;
                        _sceneChildEntitiesCollected++;
                    }
                }

                CollectSceneNodeChildren(slot, ref entityIdx, node.Child, ref snap, depth + 1, ref scannedNodes);
                node = node.NextSibling;
            }
            catch (Exception ex)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                LogSuppressedException("scene-node child collect isolated", ex, null);
                return;
            }
        }
    }

    private void CollectSceneNodeParentOwners(int slot, ref int entityIdx, CGameSceneNode? node, ref PlayerSnapshot snap)
    {
        int depth = 0;
        while (node != null)
        {
            if (depth++ >= MaxSceneNodeDepth)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                return;
            }

            try
            {
                CBaseEntity? owner = TryGetSceneNodeOwnerEntity(node);
                if (owner != null && owner.IsValid)
                    AddAssociatedEntityUnique(slot, ref entityIdx, owner, ref snap);

                node = node.PParent;
            }
            catch (Exception ex)
            {
                MarkDependentEntityCollectionFailed(ref snap);
                LogSuppressedException("scene-node parent collect isolated", ex, null);
                return;
            }
        }
    }

    private CBaseEntity? TryGetSceneNodeOwnerEntity(CGameSceneNode node)
    {
        var owner = node.Owner;
        if (owner == null || !owner.IsValid)
            return null;

        var baseEntity = owner.As<CBaseEntity>();
        return baseEntity.IsValid ? baseEntity : null;
    }

    private void MarkDependentEntityCollectionFailed(ref PlayerSnapshot snap)
    {
        if (!snap.DependentEntityCollectionFailed)
            _dependentEntityCollectionFailureCount++;

        snap.DependentEntityCollectionFailed = true;
    }

    private void MarkAssociatedEntityOverflow(ref PlayerSnapshot snap)
    {
        if (!snap.AssociatedEntityCapExceeded)
            _associatedEntityOverflowCount++;

        snap.AssociatedEntityCapExceeded = true;
        MarkDependentEntityCollectionFailed(ref snap);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Warning suppression
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Logs a suppressed exception (rate-limited to avoid console spam).</summary>
    private void LogSuppressedException(string context, Exception ex, CEntityInstance? entity)
    {
        LogSuppressedWarning(context, ex.Message, entity);
    }

    /// <summary>
    /// Logs a warning to the console, but only once every 128 ticks (≈2 seconds)
    /// per unique context/object combination. This prevents a broken object from
    /// flooding the console with thousands of identical warnings.
    /// </summary>
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
