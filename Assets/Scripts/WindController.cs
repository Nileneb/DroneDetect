using Unity.MLAgents;
using UnityEngine;

/// <summary>
/// Applies realistic wind disturbances to SimulatedDroneController.
/// Wind strength is read from the ML-Agents curriculum parameter "wind_strength" (0..1).
///
/// Two-layer Perlin noise:
///   Drift layer  (0.05 Hz) — slow, continuous direction changes every ~20 s
///   Turbulence   (0.30 Hz) — fast gusts layered on top
///
/// Wind stays hidden from agent observations — forces a robust, wind-resistant policy.
/// Visualization: stretched dust particles + Scene-view gizmo arrow.
/// </summary>
public class WindController : MonoBehaviour
{
    public static WindController Instance { get; private set; }

    [Header("Debug")]
    public bool logEverySecond = true;

    [Header("Visualization")]
    [Tooltip("m/s of particle velocity per 1 N wind force (purely visual scale)")]
    public float particleVelocityScale = 10f;
    [Tooltip("Max particles emitted per second at wind_strength = 1")]
    public int maxEmissionRate = 60;

    // AR-Drone 2.0 physics constants — keep in sync with SimulatedDroneController
    const float DroneMass  = 0.42f;
    // Drift: ~0.05 Hz  → full direction cycle every ~20 s
    const float DriftSpeed = 0.05f;
    // Turbulence: ~0.30 Hz → noticeable gusts every ~3 s
    const float TurbSpeed  = 0.30f;
    const float BaseScale  = 2.0f;
    const float TurbScale  = 1.5f;

    float _seed;
    float _logTimer;
    ParticleSystem _ps;

    public Vector3 CurrentWindForce { get; private set; }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    void Awake()
    {
        Instance = this;
        // WHY(random seed): different wind pattern every session; not tied to episode reset
        _seed = Random.Range(0f, 1000f);
        CreateParticleSystem();
    }

    void Update()
    {
        float strength = GetWindStrength();

        if (strength <= 0f)
        {
            CurrentWindForce = Vector3.zero;
            SetParticleVelocity(Vector3.zero, 0f);
            return;
        }

        float t = Time.time;

        // Drift layer — slow heading changes
        float driftX = Mathf.PerlinNoise(_seed + t * DriftSpeed, 0f) * 2f - 1f;
        float driftZ = Mathf.PerlinNoise(0f, _seed + t * DriftSpeed) * 2f - 1f;

        // Turbulence layer — short gusts
        // WHY(offset+100): separate Perlin coordinates prevent correlation with drift layer
        float turbX  = Mathf.PerlinNoise(_seed + 100f + t * TurbSpeed, 0f) * 2f - 1f;
        float turbZ  = Mathf.PerlinNoise(0f, _seed + 100f + t * TurbSpeed) * 2f - 1f;

        float fx = (driftX * BaseScale + turbX * TurbScale) * strength * DroneMass;
        float fz = (driftZ * BaseScale + turbZ * TurbScale) * strength * DroneMass;

        CurrentWindForce = new Vector3(fx, 0f, fz);
        SetParticleVelocity(CurrentWindForce, strength);

        if (logEverySecond)
        {
            _logTimer += Time.deltaTime;
            if (_logTimer >= 1f)
            {
                _logTimer = 0f;
                Debug.Log($"[Wind] strength={strength:F2}  force=({fx:F2}, 0, {fz:F2})  dir={Mathf.Atan2(fz, fx) * Mathf.Rad2Deg:F0}°");
            }
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Gizmo — Scene-view wind arrow ────────────────────────────────────────

    void OnDrawGizmos()
    {
        if (!Application.isPlaying || CurrentWindForce.magnitude < 0.05f) return;

        var origin = transform.position + Vector3.up * 5f;
        var dir    = CurrentWindForce.normalized;
        var tip    = origin + dir * 4f;

        Gizmos.color = new Color(0.35f, 0.75f, 1f, 0.9f);
        Gizmos.DrawLine(origin, tip);
        Gizmos.DrawWireSphere(tip, 0.35f);

        // Strength indicator: small perpendicular cross-bar
        var perp = new Vector3(-dir.z, 0f, dir.x) * CurrentWindForce.magnitude * 0.5f;
        Gizmos.color = new Color(0.35f, 0.75f, 1f, 0.4f);
        Gizmos.DrawLine(tip - perp, tip + perp);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    static float GetWindStrength()
    {
        try   { return Academy.Instance.EnvironmentParameters.GetWithDefault("wind_strength", 0f); }
        catch { return 0f; }
    }

    void SetParticleVelocity(Vector3 windForce, float strength)
    {
        if (_ps == null) return;

        // Drive particle velocity from wind force (visual only — not physics)
        var vol = _ps.velocityOverLifetime;
        vol.x = new ParticleSystem.MinMaxCurve(windForce.x * particleVelocityScale);
        vol.z = new ParticleSystem.MinMaxCurve(windForce.z * particleVelocityScale);

        var em = _ps.emission;
        em.rateOverTime = strength * maxEmissionRate;
    }

    void CreateParticleSystem()
    {
        var go = new GameObject("WindParticles");
        go.transform.SetParent(transform);
        go.transform.localPosition = Vector3.up * 2f; // spawn ~2 m above ground

        _ps = go.AddComponent<ParticleSystem>();

        // Main module
        var main = _ps.main;
        main.loop                = true;
        main.startLifetime       = new ParticleSystem.MinMaxCurve(2.5f, 4.5f);
        main.startSpeed          = 0f;  // velocity is entirely from velocityOverLifetime
        main.startSize           = new ParticleSystem.MinMaxCurve(0.04f, 0.12f);
        main.startColor          = new ParticleSystem.MinMaxGradient(
                                       new Color(0.85f, 0.92f, 1f, 0.25f),
                                       new Color(0.95f, 0.97f, 1f, 0.50f));
        main.maxParticles        = maxEmissionRate * 5;
        main.simulationSpace     = ParticleSystemSimulationSpace.World;
        main.gravityModifier     = 0f; // weightless dust

        // Emission — rate set dynamically in SetParticleVelocity
        var em = _ps.emission;
        em.rateOverTime = 0f;

        // Shape — box spanning the whole arena
        var shape = _ps.shape;
        shape.enabled   = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale     = new Vector3(55f, 5f, 55f);

        // Velocity over lifetime — driven by wind direction each frame
        var vol = _ps.velocityOverLifetime;
        vol.enabled = true;
        vol.space   = ParticleSystemSimulationSpace.World;
        vol.x       = new ParticleSystem.MinMaxCurve(0f);
        vol.y       = new ParticleSystem.MinMaxCurve(0f);
        vol.z       = new ParticleSystem.MinMaxCurve(0f);

        // Renderer — stretch particles in direction of travel (dust streak effect)
        var rend = _ps.GetComponent<ParticleSystemRenderer>();
        rend.renderMode    = ParticleSystemRenderMode.Stretch;
        rend.velocityScale = 0.25f;
        rend.lengthScale   = 2.5f;
        rend.sortingOrder  = 1;
    }
}
