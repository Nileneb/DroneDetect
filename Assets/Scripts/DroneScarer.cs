using System.Collections;
using UnityEngine;

public class DroneScarer : MonoBehaviour
{
    [Header("Scarer")]
    public float radius = 6f;
    public float duration = 3f;
    public float cooldown = 8f;
    public float pushForce = 8f;
    public Light scarerLight;
    public AudioSource scarerAudio;
    public ParticleSystem scarerParticles;

    public bool IsActive { get; private set; }
    public bool IsReady => !IsActive && _cooldownRemaining <= 0f;
    public float ActiveFraction => IsActive ? _activeElapsed / duration : 0f;
    public float CooldownFraction => _cooldownRemaining > 0f ? _cooldownRemaining / cooldown : 0f;

    int _dronePlayerId;
    float _cooldownRemaining;
    float _activeElapsed;

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
            var json = $"{{\"id\":{_dronePlayerId},\"radius\":{radius:F1},\"duration\":{duration:F1}}}";
            RevbClient.Instance.Send("scarer.activated", json);
        }
    }

    IEnumerator ScarerRoutine()
    {
        IsActive = true;
        _activeElapsed = 0f;
        SetVisuals(true);

        while (_activeElapsed < duration)
        {
            PushNearbyWolves();
            _activeElapsed += Time.deltaTime;
            yield return null;
        }

        IsActive = false;
        _cooldownRemaining = cooldown;
        SetVisuals(false);
    }

    void PushNearbyWolves()
    {
        var hits = Physics.OverlapSphere(transform.position, radius);
        foreach (var h in hits)
        {
            var wolf = h.GetComponent<WolfPlayer>();
            if (wolf != null && wolf.IsLocal)
                wolf.PushAway(transform.position, pushForce);
        }
    }

    void SetVisuals(bool on)
    {
        if (scarerLight) scarerLight.enabled = on;
        if (on && scarerParticles) scarerParticles.Play();
        else if (!on && scarerParticles) scarerParticles.Stop();
        if (scarerAudio)
        {
            if (on) scarerAudio.Play();
            else scarerAudio.Stop();
        }
    }
}
