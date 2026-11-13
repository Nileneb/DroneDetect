using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

public sealed class DroneFollowDemoRoute : MonoBehaviour
{
    const float TargetSpeed = 0.7f;
    const float RouteRadius = 3f;

    static readonly Vector3[] RouteOffsets =
    {
        new Vector3(3f, 0f, 0f),
        new Vector3(3f, 0f, 3f),
        new Vector3(0f, 0f, 3f),
        new Vector3(-3f, 0f, 3f),
        new Vector3(-3f, 0f, 0f),
        new Vector3(-3f, 0f, -3f),
        new Vector3(0f, 0f, -3f),
        new Vector3(3f, 0f, -3f)
    };

    readonly List<Vector3> _waypoints = new List<Vector3>();
    NavMeshAgent _targetAgent;
    int _nextWaypoint;

    public bool Begin(DroneFollowAgent follower)
    {
        if (follower == null || follower.followTarget != transform)
        {
            Debug.LogError("[DroneFollowDemo] Route target must be the DroneFollowAgent's assigned followTarget.", this);
            return false;
        }

        GameObject ground = GameObject.FindGameObjectWithTag("Ground");
        Collider groundCollider = ground != null ? ground.GetComponent<Collider>() : null;
        if (groundCollider == null)
        {
            Debug.LogError("[DroneFollowDemo] Cannot build demo NavMesh: a Ground-tagged object with a Collider is required.", this);
            return false;
        }

        NavMeshSurface surface = ground.GetComponent<NavMeshSurface>();
        if (surface == null)
            surface = ground.AddComponent<NavMeshSurface>();
        surface.agentTypeID = 0;
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
        surface.layerMask = 1 << ground.layer;
        surface.BuildNavMesh();

        SimulatedMovingTarget randomMotion = GetComponent<SimulatedMovingTarget>();
        if (randomMotion != null)
            randomMotion.enabled = false;

        Vector3 flatForward = Vector3.ProjectOnPlane(follower.transform.forward, Vector3.up).normalized;
        if (flatForward.sqrMagnitude < 0.01f)
            flatForward = Vector3.forward;

        Vector3 requestedStart = follower.transform.position + flatForward * 4f;
        requestedStart.y = groundCollider.bounds.max.y;
        if (!NavMesh.SamplePosition(requestedStart, out NavMeshHit start, 3f, NavMesh.AllAreas))
        {
            Debug.LogError("[DroneFollowDemo] Could not place the target on the Ground NavMesh. Check the Ground collider and drone position.", this);
            return false;
        }

        _targetAgent = GetComponent<NavMeshAgent>();
        if (_targetAgent == null)
            _targetAgent = gameObject.AddComponent<NavMeshAgent>();
        _targetAgent.enabled = false;
        _targetAgent.agentTypeID = 0;
        _targetAgent.radius = 0.2f;
        _targetAgent.height = 1.2f;
        _targetAgent.baseOffset = 0f;
        _targetAgent.speed = TargetSpeed;
        _targetAgent.acceleration = 3f;
        _targetAgent.angularSpeed = 180f;
        _targetAgent.stoppingDistance = 0.35f;
        _targetAgent.autoBraking = true;

        transform.position = start.position;
        _targetAgent.enabled = true;
        if (!_targetAgent.isOnNavMesh && !_targetAgent.Warp(start.position))
        {
            Debug.LogError("[DroneFollowDemo] NavMeshAgent could not attach to the generated NavMesh.", this);
            return false;
        }

        BuildRoute(start.position);
        if (_waypoints.Count == 0)
        {
            Debug.LogError("[DroneFollowDemo] NavMesh was built, but none of the scripted route points are reachable.", this);
            return false;
        }

        _nextWaypoint = 0;
        SetNextWaypoint();
        Debug.Log($"[DroneFollowDemo] Target route ready: {_waypoints.Count} reachable NavMesh waypoints at {TargetSpeed:F1} m/s.", this);
        return true;
    }

    void Update()
    {
        if (_targetAgent == null || !_targetAgent.enabled || _targetAgent.pathPending ||
            !_targetAgent.hasPath ||
            _targetAgent.remainingDistance > _targetAgent.stoppingDistance + 0.15f)
            return;

        SetNextWaypoint();
    }

    void BuildRoute(Vector3 origin)
    {
        _waypoints.Clear();
        foreach (Vector3 offset in RouteOffsets)
        {
            Vector3 candidate = origin + offset.normalized * RouteRadius;
            candidate.y = origin.y;
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                continue;

            var path = new NavMeshPath();
            if (NavMesh.CalculatePath(origin, hit.position, NavMesh.AllAreas, path) &&
                path.status == NavMeshPathStatus.PathComplete)
                _waypoints.Add(hit.position);
        }
    }

    void SetNextWaypoint()
    {
        if (_waypoints.Count == 0) return;
        Vector3 destination = _waypoints[_nextWaypoint];
        _nextWaypoint = (_nextWaypoint + 1) % _waypoints.Count;
        if (!_targetAgent.SetDestination(destination))
            Debug.LogError($"[DroneFollowDemo] NavMeshAgent rejected waypoint {destination}.", this);
    }
}
