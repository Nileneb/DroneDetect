using UnityEngine;

public class ShepherdCamera : MonoBehaviour
{
    [Header("3rd Person")]
    public Transform target;
    public Vector3 offset = new Vector3(0, 4f, -6f);
    public float smoothSpeed = 8f;
    public float rotationSmoothSpeed = 10f;

    [Header("Drone Modes (F1/F2/F3)")]
    public bool isDroneCamera;
    public float droneFov = 70f;

    Camera _cam;
    float _yaw;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        if (_cam == null) _cam = gameObject.AddComponent<Camera>();
        if (isDroneCamera && _cam) _cam.fieldOfView = droneFov;
    }

    void Start()
    {
        if (target == null && !isDroneCamera)
            target = transform.parent;
    }

    void LateUpdate()
    {
        if (isDroneCamera) return;
        if (target == null) return;

        _yaw = target.eulerAngles.y;

        var desiredPos = target.position + Quaternion.Euler(0, _yaw, 0) * offset;
        transform.position = Vector3.Lerp(transform.position, desiredPos, smoothSpeed * Time.deltaTime);

        var dir = (target.position + Vector3.up * 1.2f) - transform.position;
        if (dir != Vector3.zero)
        {
            var targetRot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSmoothSpeed * Time.deltaTime);
        }
    }
}
