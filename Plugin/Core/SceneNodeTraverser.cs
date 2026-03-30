using System;
using CounterStrikeSharp.API.Core;

namespace S2FOW.Core;

public class SceneNodeTraverser
{
    private int _maxTraversalNodes = 512;
    private int _maxTraversalDepth = 16;

    public delegate void AddEntityCallback(int slot, CBaseEntity entity);

    /// <summary>
    /// Configures traversal limits. Call when config changes.
    /// </summary>
    public void Configure(int maxNodes, int maxDepth)
    {
        _maxTraversalNodes = Math.Max(64, maxNodes);
        _maxTraversalDepth = Math.Max(4, maxDepth);
    }

    public void CollectChildSceneEntities(
        int slot,
        CGameSceneNode sceneNode,
        int pawnIndex,
        AddEntityCallback addEntityCallback,
        Action<string> onLimitHit)
    {
        int visitedNodeCount = 0;
        CollectChildSceneEntitiesRecursive(slot, sceneNode, pawnIndex, 0, ref visitedNodeCount, addEntityCallback);
        if (visitedNodeCount >= _maxTraversalNodes)
            onLimitHit("child scene traversal limit");
    }

    private void CollectChildSceneEntitiesRecursive(
        int slot,
        CGameSceneNode parentNode,
        int pawnIndex,
        int depth,
        ref int visitedNodeCount,
        AddEntityCallback addEntityCallback)
    {
        if (depth >= _maxTraversalDepth || visitedNodeCount >= _maxTraversalNodes)
            return;

        var child = parentNode.Child;
        while (child != null && visitedNodeCount < _maxTraversalNodes)
        {
            visitedNodeCount++;
            var owner = child.Owner;
            if (owner != null && owner.IsValid && owner.Index != pawnIndex)
            {
                var ownerEntity = owner.As<CBaseEntity>();
                if (ownerEntity.IsValid)
                    addEntityCallback(slot, ownerEntity);
            }

            if (child.Child != null)
                CollectChildSceneEntitiesRecursive(slot, child, pawnIndex, depth + 1, ref visitedNodeCount, addEntityCallback);

            child = child.NextSibling;
        }
    }
}
