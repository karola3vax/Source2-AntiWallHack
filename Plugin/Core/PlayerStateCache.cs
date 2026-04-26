using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW.Core;

/// <summary>
/// Builds and stores a per-frame snapshot of every connected player's state.
///
/// Every network frame, the CheckTransmit hook needs to know each player's position,
/// speed, team, weapon, and collision bounds. Instead of reading this data multiple
/// times from live engine entities (which can change mid-frame), we capture everything
/// into flat value-type snapshots once at the start of each frame.
///
/// This gives us:
///   - Consistency: all visibility checks within a frame use the same data.
///   - Performance: reading from a flat struct array is faster than traversing
///     managed object hierarchies repeatedly.
///   - Safety: stale entity references can throw exceptions; by capturing once
///     and wrapping in try/catch, we isolate failures to a single snapshot.
///
/// Additionally, this class tracks which entity indices (pawn + weapons +
/// wearables + hostage entities + scene-node child owners) are "associated" with
/// each player. When a
/// player is hidden, ALL of these entities must be removed from the transmit
/// list — otherwise orphaned child entities cause a fatal client crash.
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

    /// <summary>Maximum scene-node descendants scanned from any one associated entity.</summary>
    private const int MaxSceneNodesPerRoot = 96;

    /// <summary>Maximum child depth followed in the scene-node hierarchy.</summary>
    private const int MaxSceneNodeDepth = 8;

    /// <summary>Maximum siblings followed at each scene-node level.</summary>
    private const int MaxSceneNodeSiblingsPerLevel = 96;

    // ────────────────────────────────────────────────────────────────────────
    //  Storage
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>One snapshot per possible player slot (0–63).</summary>
    private readonly PlayerSnapshot[] _snapshots = new PlayerSnapshot[FowConstants.MaxSlots];

    /// <summary>
    /// 2D array of entity indices associated with each player.
    /// For slot 5 with 7 entities: _associatedEntities[5, 0] through [5, 6].
    /// These include the pawn itself, all weapon entities, wearables, hostage entities,
    /// and valid scene-node child owners reachable from those entities.
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

    /// <summary>How many player snapshots had incomplete dependent-entity collection.</summary>
    private long _dependentEntityCollectionFailureCount;

    /// <summary>How many player snapshots exceeded the associated entity capacity.</summary>
    private long _associatedEntityOverflowCount;

    /// <summary>Total scene-node child owner entities added to player closures.</summary>
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

    /// <summary>How many snapshots were marked unsafe because dependent entity collection failed.</summary>
    public long DependentEntityCollectionFailureCount => _dependentEntityCollectionFailureCount;

    /// <summary>How many snapshots exceeded the associated entity closure capacity.</summary>
    public long AssociatedEntityOverflowCount => _associatedEntityOverflowCount;

    /// <summary>Total scene-node child owner entities added to closures since the last reset.</summary>
    public long SceneChildEntitiesCollected => _sceneChildEntitiesCollected;

    /// <summary>Gets the entity index of the Nth associated entity for a given player slot.</summary>
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
    /// Captures a fresh snapshot of every connected player's state.
    ///
    /// For each player, we read:
    ///   - Position (feet and eyes), view angles, velocity.
    ///   - Team, alive/dead status, bot flag.
    ///   - Collision bounds (bounding box).
    ///   - Active weapon type and max movement speed.
    ///   - All entity indices (pawn + weapons + wearables + hostage prop) that should be hidden together.
    /// </summary>
    public void BuildSnapshots(List<CCSPlayerController> players)
    {
        // Clear previous frame's data.
        ResetActiveSlots();

        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            if (!controller.IsValid)
                continue;

            int slot = controller.Slot;
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

            // Weapon data (affects movement speed and which LOS points to use).
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

            // Collect all entity indices associated with this player.
            // This includes the pawn itself, all weapons in the inventory,
            // the active weapon, the previously held weapon, all wearable
            // entities (gloves, agent accessories), hostage entities, and
            // scene-node child owners reachable from those entities.
            // Every one of these must be hidden together — if we hide the pawn
            // but leave a child entity transmitted, the client crashes with:
            //   FATAL ERROR: CL_CopyExistingEntity: missing client entity
            // because the orphaned entity references a parent pawn that was
            // removed from the transmit list.
            int entityIdx = 0;
            AddAssociatedEntity(slot, ref entityIdx, pawn, ref snap);
            CollectWeaponEntities(slot, ref entityIdx, pawn, ref snap);
            CollectWearableEntities(slot, ref entityIdx, pawn, ref snap);
            CollectHostageEntities(slot, ref entityIdx, pawn, ref snap);
            CollectSceneNodeClosure(slot, ref entityIdx, pawn, ref snap);
            snap.AssociatedEntityCount = entityIdx;
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
    /// Collects all weapon entity indices for a player.
    /// Each weapon the player carries is a separate entity that must be hidden
    /// together with the player's body (pawn). Otherwise, a hidden player's
    /// floating gun would still be visible.
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
    /// Collects all wearable entity indices for a player.
    ///
    /// In CS2, wearable items (gloves, agent accessories, charms) are stored in
    /// the pawn's m_hMyWearables array (inherited from C_BaseCombatCharacter at
    /// schema offset 4440). Each wearable is a C_EconWearable — a fully independent
    /// networked entity with its own entity index.
    ///
    /// If we hide the pawn but leave its wearables in the transmit list, the client
    /// receives orphaned entity data for wearables whose parent pawn is missing,
    /// causing: FATAL ERROR: CL_CopyExistingEntity: missing client entity.
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
    /// Collects hostage-related entity indices for a player.
    ///
    /// On hostage maps (cs_office, cs_italy, etc.), when a CT picks up a hostage,
    /// the engine creates a C_HostageCarriableProp entity (parent: CBaseAnimGraph)
    /// and parents it to the pawn via SetParent. The prop's m_hOwnerEntity (offset
    /// 1312 in C_BaseEntity) references the carrying pawn.
    ///
    /// If the pawn is hidden but the carry prop remains transmitted, the client
    /// receives entity data whose owner does not exist → same crash class as
    /// orphaned wearables.
    ///
    /// This is a rare scenario (hostage maps only, during active carry, while
    /// simultaneously hidden by visibility checks) but costs nearly zero CPU
    /// to check defensively.
    /// </summary>
    private void CollectHostageEntities(int slot, ref int entityIdx, CCSPlayerPawn pawn, ref PlayerSnapshot snap)
    {
        try
        {
            var hostageServices = pawn.HostageServices;
            if (hostageServices == null)
                return;

            // The hostage carry prop — a visual model parented to the pawn's back.
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

    /// <summary>Returns true only when the pawn still has a valid engine controller handle.</summary>
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

    /// <summary>Adds an entity's index to the associated entities list for a player slot.</summary>
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
    /// Same as AddAssociatedEntity, but skips duplicates.
    /// A weapon can appear in multiple lists (MyWeapons + ActiveWeapon), so we
    /// check if it is already recorded before adding.
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
    /// Adds valid owner entities reachable through the scene-node child/sibling tree.
    /// These are common parented or bone-merged visuals that can crash clients if
    /// transmitted after their parent pawn is hidden.
    /// </summary>
    private void CollectSceneNodeClosure(int slot, ref int entityIdx, CBaseEntity entity, ref PlayerSnapshot snap)
    {
        try
        {
            var sceneNode = entity.CBodyComponent?.SceneNode;
            if (sceneNode == null)
                return;

            int scannedNodes = 0;
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
    /// per unique context+entity combination. This prevents a broken entity from
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
