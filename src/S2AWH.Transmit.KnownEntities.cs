using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace S2AWH;

public partial class S2AWH
{
    private bool TryEnsureKnownEntityHandlesInitialized(int nowTick)
    {
        if (_knownEntityHandlesInitialized)
        {
            return true;
        }

        if (_knownEntityHandles.Count > 0)
        {
            ValidateKnownEntityHandleState();
            if (_knownEntityHandles.Count > 0)
            {
                _knownEntityHandlesInitialized = true;
                _knownEntityBootstrapRetryUntilTick = -1;
                return true;
            }
        }

        if (_knownEntityBootstrapRetryUntilTick >= nowTick)
        {
            return false;
        }

        List<uint> discoveredHandles = _knownEntityHandleBootstrapScratch;
        Dictionary<uint, int> discoveredIndices = _knownEntityHandleBootstrapIndicesScratch;
        discoveredHandles.Clear();
        discoveredIndices.Clear();

        PrimeKnownLivePlayerHandles();
        int seededHandleCount = _knownEntityHandles.Count;
        for (int i = 0; i < seededHandleCount; i++)
        {
            uint entityHandleRaw = _knownEntityHandles[i];
            if (!IsValidTrackedEntityHandle(entityHandleRaw) || discoveredIndices.ContainsKey(entityHandleRaw))
            {
                continue;
            }

            discoveredIndices[entityHandleRaw] = discoveredHandles.Count;
            discoveredHandles.Add(entityHandleRaw);
        }

        try
        {
            foreach (CEntityInstance entityInstance in Utilities.GetAllEntities())
            {
                if (entityInstance == null || !entityInstance.IsValid)
                {
                    continue;
                }

                uint entityHandleRaw = entityInstance.EntityHandle.Raw;
                if (!IsValidTrackedEntityHandle(entityHandleRaw) || discoveredIndices.ContainsKey(entityHandleRaw))
                {
                    continue;
                }

                discoveredIndices[entityHandleRaw] = discoveredHandles.Count;
                discoveredHandles.Add(entityHandleRaw);
            }
        }
        catch
        {
            discoveredHandles.Clear();
            discoveredIndices.Clear();
            _knownEntityHandlesInitialized = false;
            _knownEntityBootstrapRetryUntilTick = nowTick + KnownEntityBootstrapRetryDelayTicks;
            return false;
        }

        _knownEntityHandles.Clear();
        _knownEntityHandles.AddRange(discoveredHandles);
        _knownEntityHandleIndices.Clear();
        foreach ((uint entityHandleRaw, int index) in discoveredIndices)
        {
            _knownEntityHandleIndices[entityHandleRaw] = index;
        }

        _knownEntityHandlesInitialized = true;
        _knownEntityBootstrapRetryUntilTick = -1;
        return true;
    }

    private static bool IsValidTrackedEntityHandle(uint entityHandleRaw)
    {
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        return entityIndex > 0 && entityIndex < Utilities.MaxEdicts;
    }

    private void TrackKnownEntityHandle(CEntityInstance entityInstance)
    {
        if (!TryGetTrackedEntityHandleRaw(entityInstance, out uint entityHandleRaw))
        {
            return;
        }

        TrackKnownEntityHandle(entityHandleRaw);
    }

    private void TrackKnownEntityHandle(uint entityHandleRaw)
    {
        if (!IsValidTrackedEntityHandle(entityHandleRaw) ||
            _knownEntityHandleIndices.ContainsKey(entityHandleRaw))
        {
            return;
        }

        int index = _knownEntityHandles.Count;
        _knownEntityHandles.Add(entityHandleRaw);
        _knownEntityHandleIndices[entityHandleRaw] = index;
    }

    private void UntrackKnownEntityHandle(uint entityHandleRaw)
    {
        if (!_knownEntityHandleIndices.TryGetValue(entityHandleRaw, out int removeIndex))
        {
            return;
        }

        int lastIndex = _knownEntityHandles.Count - 1;
        uint lastHandleRaw = _knownEntityHandles[lastIndex];
        _knownEntityHandles[removeIndex] = lastHandleRaw;
        _knownEntityHandles.RemoveAt(lastIndex);
        _knownEntityHandleIndices[lastHandleRaw] = removeIndex;
        _knownEntityHandleIndices.Remove(entityHandleRaw);
    }

    private void PruneKnownEntityHandles(List<uint> staleHandles)
    {
        int staleCount = staleHandles.Count;
        for (int i = 0; i < staleCount; i++)
        {
            UntrackKnownEntityHandle(staleHandles[i]);
        }

        staleHandles.Clear();
    }

    private static bool TryGetTrackedEntityHandleRaw(CEntityInstance? entityInstance, out uint entityHandleRaw)
    {
        entityHandleRaw = 0;
        if (entityInstance == null)
        {
            return false;
        }

        try
        {
            entityHandleRaw = entityInstance.EntityHandle.Raw;
        }
        catch
        {
            return false;
        }

        return IsValidTrackedEntityHandle(entityHandleRaw);
    }
}
