using UnityEngine;

/// <summary>
/// Hängt sich an die Main Camera in ShepherdArena. Findet den lokalen Spieler
/// (WolfPlayer.IsLocal == true ODER DronePlayer.IsLocal == true) und folgt ihm.
///
/// Default: dritte-Person-Sicht, leicht hinten/oben. Drohne fliegt → Camera
/// folgt aus der Distanz; Wolf läuft → Camera shoulder-cam.
///
/// Workaround für die Verwechslung: wenn user "Drone" klickt aber das Spiel
/// zeigt nur die AI-Wolf, dann liegt's daran dass das Main Camera-Asset
/// niemandem folgt. Dieses Script behebt das.
/// </summary>
public class LocalPlayerCamera : MonoBehaviour
{
    [Header("Drone follow")]
    public Vector3 droneOffset = new Vector3(0f, 2f, -6f);
    public Vector3 droneLook   = new Vector3(0f, 0f, 4f);

    [Header("Wolf follow")]
    public Vector3 wolfOffset  = new Vector3(0f, 3.5f, -5f);
    public Vector3 wolfLook    = new Vector3(0f, 0.5f, 2f);

    [Header("Smoothing")]
    public float positionLerp = 6f;
    public float rotationLerp = 4f;

    [Header("Discovery")]
    [Tooltip("How often (s) to re-search for the local player. Drone/Wolf spawn lazily after scene load.")]
    public float rediscoverInterval = 0.5f;

    Transform _follow;
    bool _isDrone;
    float _nextDiscoveryAt;

    void LateUpdate()
    {
        if (_follow == null && Time.time >= _nextDiscoveryAt)
        {
            _nextDiscoveryAt = Time.time + rediscoverInterval;
            DiscoverLocal();
        }
        if (_follow == null) return;

        var offset = _isDrone ? droneOffset : wolfOffset;
        var look   = _isDrone ? droneLook   : wolfLook;

        // Position: target's local space → world
        var desiredPos = _follow.position + _follow.rotation * offset;
        transform.position = Vector3.Lerp(transform.position, desiredPos, positionLerp * Time.deltaTime);

        var lookPoint = _follow.position + _follow.rotation * look;
        var desiredRot = Quaternion.LookRotation(lookPoint - transform.position, Vector3.up);
        transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotationLerp * Time.deltaTime);
    }

    void DiscoverLocal()
    {
        // Drone first — if there's a local DronePlayer, that's us
        var drones = Object.FindObjectsByType<DronePlayer>(FindObjectsSortMode.None);
        foreach (var d in drones)
        {
            if (d.IsLocal) { _follow = d.transform; _isDrone = true; return; }
        }
        var wolves = Object.FindObjectsByType<WolfPlayer>(FindObjectsSortMode.None);
        foreach (var w in wolves)
        {
            if (w.IsLocal) { _follow = w.transform; _isDrone = false; return; }
        }
    }
}
