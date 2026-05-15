using UnityEngine;

public class MovingObstacle : MonoBehaviour
{
    [Header("Path")]
    public Transform pointA;
    public Transform pointB;

    [Header("Settings")]
    public float speed = 1.5f;
    public bool startAtA = true;

    Transform _target;

    void OnEnable()
    {
        if (pointA == null || pointB == null) return;
        transform.position = startAtA ? pointA.position : pointB.position;
        _target = startAtA ? pointB : pointA;
    }

    void Update()
    {
        if (pointA == null || pointB == null) return;

        transform.position = Vector3.MoveTowards(transform.position, _target.position, speed * Time.deltaTime);

        if (Vector3.Distance(transform.position, _target.position) < 0.05f)
            _target = _target == pointA ? pointB : pointA;
    }
}
