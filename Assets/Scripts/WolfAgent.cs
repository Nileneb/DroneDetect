using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class WolfAgent : Agent
{
    [Header("Settings")]
    public float moveSpeed = 4f;
    public float catchRadius = 1.2f;

    NavMeshAgent _nav;
    Rigidbody _rb;
    Transform _nearestSheep;
    Transform _drone;
    DroneScarer _scarer;
    ShepherdGameManager _gm;

    public override void Initialize()
    {
        _nav = GetComponent<NavMeshAgent>();
        _rb = GetComponent<Rigidbody>();
        _gm = FindAnyObjectByType<ShepherdGameManager>();
    }

    public override void OnEpisodeBegin()
    {
        transform.localPosition = new Vector3(Random.Range(-8f, 8f), 0, Random.Range(-8f, 8f));
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        // Position relative to arena center (3)
        sensor.AddObservation(transform.localPosition / 10f);

        // Velocity (3)
        var vel = _rb ? _rb.linearVelocity : Vector3.zero;
        sensor.AddObservation(vel / moveSpeed);

        // Nearest sheep (4)
        _nearestSheep = FindNearest("Sheep");
        if (_nearestSheep != null)
        {
            var toSheep = transform.InverseTransformDirection(_nearestSheep.position - transform.position);
            sensor.AddObservation(toSheep.normalized);
            sensor.AddObservation(Vector3.Distance(transform.position, _nearestSheep.position) / 20f);
        }
        else
        {
            sensor.AddObservation(Vector3.zero);
            sensor.AddObservation(1f);
        }

        // Drone (2)
        _drone = FindNearest("Drone");
        _scarer = _drone != null ? _drone.GetComponent<DroneScarer>() : null;
        if (_drone != null)
        {
            sensor.AddObservation(Vector3.Distance(transform.position, _drone.position) / 20f);
            sensor.AddObservation(_scarer != null && _scarer.IsActive ? 1f : 0f);
        }
        else
        {
            sensor.AddObservation(1f);
            sensor.AddObservation(0f);
        }
    }

    public override void OnActionReceived(ActionBuffers actions)
    {
        float forward = Mathf.Clamp(actions.ContinuousActions[0], -1f, 1f);
        float turn    = Mathf.Clamp(actions.ContinuousActions[1], -1f, 1f);

        transform.Rotate(Vector3.up, turn * 120f * Time.fixedDeltaTime);

        if (_nav && _nav.enabled)
        {
            var target = transform.position + transform.forward * forward * moveSpeed * Time.fixedDeltaTime;
            _nav.Move(transform.forward * forward * moveSpeed * Time.fixedDeltaTime);
        }

        AddReward(-0.001f);

        if (_nearestSheep != null && Vector3.Distance(transform.position, _nearestSheep.position) < catchRadius)
        {
            AddReward(1f);
            EndEpisode();
        }

        if (_scarer != null && _scarer.IsActive)
        {
            float dist = _drone != null ? Vector3.Distance(transform.position, _drone.position) : 100f;
            if (dist < _scarer.radius)
                AddReward(-0.5f * (1f - dist / _scarer.radius));
        }
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var ca = actionsOut.ContinuousActions;
        ca[0] = Input.GetAxis("Vertical");
        ca[1] = Input.GetAxis("Horizontal");
    }

    Transform FindNearest(string tag)
    {
        var objs = GameObject.FindGameObjectsWithTag(tag);
        Transform nearest = null;
        float minD = float.MaxValue;
        foreach (var o in objs)
        {
            if (!o.activeInHierarchy) continue;
            float d = Vector3.Distance(transform.position, o.transform.position);
            if (d < minD) { minD = d; nearest = o.transform; }
        }
        return nearest;
    }
}
