using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Bakes the NavMeshSurface once at startup so Tiger's NavMesh pathing is always
/// available in play mode and builds, without relying on a persisted NavMesh asset.
/// Runs before other scripts (execution order) so NavMeshAgents find a valid NavMesh
/// when they initialize; also warps existing agents onto the fresh mesh.
/// </summary>
[DefaultExecutionOrder(-500)]
[RequireComponent(typeof(NavMeshSurface))]
public class RuntimeNavMeshBake : MonoBehaviour
{
    private void Awake()
    {
        var surface = GetComponent<NavMeshSurface>();
        if (surface != null) surface.BuildNavMesh();

        // attach any agents that came up before the mesh existed
        foreach (var agent in FindObjectsByType<NavMeshAgent>(FindObjectsInactive.Include))
        {
            if (!agent.isOnNavMesh &&
                NavMesh.SamplePosition(agent.transform.position, out var hit, 5f, NavMesh.AllAreas))
                agent.Warp(hit.position);
        }
    }
}
