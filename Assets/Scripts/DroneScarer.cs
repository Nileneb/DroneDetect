using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Downward-pointing fear weapon. While active:
///   • A bright SpotLight cone is enabled (visual)
///   • A child trigger collider (sphere) catches any WolfPlayer inside the
///     radius and applies WolfFear.AddFear each frame
///   • A LineRenderer beam points to the nearest wolf for extra feedback
/// </summary>
public class DroneScarer : MonoBehaviour
{
    [Header("Scarer Range")]
    public float radius = 6f;
    public float duration = 3f;
    public float cooldown = 8f;
    public float pushForce = 8f;
    public float fearGainRate = 0.5f;

    [Header("Visuals (auto-created if null)")]
    public Light scarerLight;
    public AudioSource scarerAudio;
    public ParticleSystem scarerParticles;
    public LineRenderer fearBeam;

    public bool IsActive { get; private set; }
    public bool IsReady => !IsActive && _cooldownRemaining <= 0f;
    public float ActiveFraction => IsActive ? _activeElapsed / duration : 0f;
    public float CooldownFraction => _cooldownRemaining > 0f ? _cooldownRemaining / cooldown : 0f;

    int _dronePlayerId;
    float _cooldownRemaining;
    float _activeElapsed;
    ScarerTriggerRelay _trigger;
    readonly HashSet<WolfPlayer> _wolvesInCone = new();

    void Awake()
    {
        // Procedural visual + trigger setup if not wired in prefab
        EnsureSpotLight();
        EnsureTrigger();
    }

    void Update()
    {
        if (_cooldownRemaining > 0f)
            _cooldownRemaining -= Time.deltaTime;
    }

    public void SetPlayerId(int id) => _dronePlayerId = id;

    public void Activate()
    {
        if (IsActive || !IsReady) return;
        StartCoroutine(ScarerRoutine());

        if (RevbClient.Instance != null)
        {
            var p = transform.position;
            var json = $"{{\"id\":{_dronePlayerId},\"radius\":{radius:F1},\"duration\":{duration:F1},\"x\":{p.x:F2},\"y\":{p.y:F2},\"z\":{p.z:F2}}}";
            RevbClient.Instance.Send("scarer.activated", json);
        }
    }

    /// <summary>Issue #4: visual-only toggle for remote-ghost drones.</summary>
    public void SetVisualsOnly(bool on)
    {
        if (on == IsActive) return;
        IsActive = on;
        SetVisuals(on);
        if (!on && fearBeam != null) fearBeam.enabled = false;
        // remote ghost shouldn't run the push/fear logic — trigger disabled
        if (_trigger != null) _trigger.gameObject.SetActive(false);
    }

    IEnumerator ScarerRoutine()
    {
        IsActive = true;
        _activeElapsed = 0f;
        _wolvesInCone.Clear();
        SetVisuals(true);
        if (_trigger != null) _trigger.gameObject.SetActive(true);

        while (_activeElapsed < duration)
        {
            ApplyFearAndPush();
            _activeElapsed += Time.deltaTime;
            yield return null;
        }

        IsActive = false;
        _cooldownRemaining = cooldown;
        _wolvesInCone.Clear();
        SetVisuals(false);
        if (_trigger != null) _trigger.gameObject.SetActive(false);
    }

    void ApplyFearAndPush()
    {
        WolfPlayer nearest = null;
        float nearestDist = float.MaxValue;
        foreach (var wolf in _wolvesInCone)
        {
            if (wolf == null) continue;
            wolf.PushAway(transform.position, pushForce);
            wolf.GetComponent<WolfFear>()?.AddFear(fearGainRate);

            float d = Vector3.Distance(transform.position, wolf.transform.position);
            if (d < nearestDist) { nearestDist = d; nearest = wolf; }
        }

        if (fearBeam != null)
        {
            fearBeam.enabled = nearest != null;
            if (nearest != null)
            {
                fearBeam.SetPosition(0, transform.position);
                fearBeam.SetPosition(1, nearest.transform.position + Vector3.up * 0.5f);
            }
        }
    }

    void SetVisuals(bool on)
    {
        if (scarerLight) scarerLight.enabled = on;
        if (fearBeam != null && !on) fearBeam.enabled = false;
        if (on && scarerParticles) scarerParticles.Play();
        else if (!on && scarerParticles) scarerParticles.Stop();
        if (scarerAudio)
        {
            if (on) scarerAudio.Play();
            else scarerAudio.Stop();
        }
    }

    // ── Procedural setup ──────────────────────────────────────────────────

    void EnsureSpotLight()
    {
        if (scarerLight != null) { scarerLight.enabled = false; return; }
        var go = new GameObject("ScarerLight");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);  // point straight down
        scarerLight = go.AddComponent<Light>();
        scarerLight.type = LightType.Spot;
        scarerLight.range = radius * 1.5f;
        scarerLight.spotAngle = 90f;
        scarerLight.innerSpotAngle = 60f;
        scarerLight.intensity = 12f;
        scarerLight.color = new Color(1f, 0.95f, 0.55f);   // warm searchlight
        scarerLight.shadows = LightShadows.None;
        scarerLight.enabled = false;
    }

    void EnsureTrigger()
    {
        if (_trigger != null) return;
        var go = new GameObject("ScarerTrigger");
        go.transform.SetParent(transform, false);
        // Position the sphere so its top is at the drone and it extends downward.
        go.transform.localPosition = new Vector3(0, -radius * 0.5f, 0);
        var col = go.AddComponent<SphereCollider>();
        col.isTrigger = true;
        col.radius = radius;
        _trigger = go.AddComponent<ScarerTriggerRelay>();
        _trigger.Init(this);
        go.SetActive(false);
    }

    internal void OnWolfEnter(WolfPlayer wolf) { if (wolf != null) _wolvesInCone.Add(wolf); }
    internal void OnWolfExit(WolfPlayer wolf)  { if (wolf != null) _wolvesInCone.Remove(wolf); }
}

/// <summary>Forwards OnTrigger events from a child collider to its parent DroneScarer.</summary>
public class ScarerTriggerRelay : MonoBehaviour
{
    DroneScarer _owner;
    public void Init(DroneScarer owner) => _owner = owner;

    void OnTriggerEnter(Collider other)
    {
        var w = other.GetComponentInParent<WolfPlayer>();
        if (w != null) _owner?.OnWolfEnter(w);
    }
    void OnTriggerExit(Collider other)
    {
        var w = other.GetComponentInParent<WolfPlayer>();
        if (w != null) _owner?.OnWolfExit(w);
    }
}
