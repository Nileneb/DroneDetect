using System.Collections;
using UnityEngine;

public class DroneScarer : MonoBehaviour
{
    [Header("Scarer")]
    public float radius = 6f;
    public float duration = 3f;
    public float pushForce = 8f;
    public Light scarerLight;
    public AudioSource scarerAudio;
    public ParticleSystem scarerParticles;

    public bool IsActive { get; private set; }

    int _dronePlayerId;

    public void SetPlayerId(int id) => _dronePlayerId = id;

    public void Activate()
    {
        if (IsActive) return;
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
        SetVisuals(true);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            PushNearbyWolves();
            elapsed += Time.deltaTime;
            yield return null;
        }

        IsActive = false;
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
