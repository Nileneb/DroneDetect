using UnityEngine;

/// <summary>
/// SimulatedDroneController — Digital-Twin der AR-Drone 2.0.
///
/// Implementiert IDroneController mit realistischer Physik:
///   - Masse: 420g
///   - Max horizontal speed: 5 m/s
///   - Max vertical speed: 0.7 m/s
///   - Max yaw rate: 100°/s
///   - Max tilt: 12°
///   - Response time: ~150ms (Motor-Lag, 1st-Order)
///
/// Vorzeichen-Konvention (Unity-nativ):
///   forward  → +pitch  (Nase runter = vorwaerts)
///   left     → -roll   (nach links kippen = links fliegen)
///   turn     → +yaw    (positiv = gegen Uhrzeigersinn von oben)
///   up       → +gaz    (positiv = steigen)
///
/// Auto-Hover aus update_teleop:
///   Wenn alle 4 Achsen < _EPS (1e-6) → Hover-Modus
///
/// State-Machine:
///   Landed → TakingOff → Hovering ↔ Flying → Landing → Landed
///   + Emergency-Toggle
///
/// Tilt-basierte Bewegung:
///   Neigung (Pitch/Roll) steuert Horizontalbewegung — wie bei echter Drohne.
///   Visuelle Neigung + physikalische Kraft sind gekoppelt.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SimulatedDroneController : MonoBehaviour, IDroneController
{
    // ═══════════════════════ AR-Drone 2.0 Physik-Parameter ═══════════════════════

    [Header("AR-Drone 2.0 Physical Parameters")]
    [Tooltip("Masse in kg (AR-Drone 2.0 = 0.420 kg)")]
    public float mass = 0.420f;

    [Tooltip("Max. horizontale Geschwindigkeit in m/s")]
    public float maxHorizontalSpeed = 5.0f;

    [Tooltip("Max. vertikale Geschwindigkeit in m/s")]
    public float maxVerticalSpeed = 0.7f;

    [Tooltip("Max. Yaw-Rate in Grad/s")]
    public float maxYawRate = 100f;

    [Tooltip("Max. Neigungswinkel in Grad (Pitch/Roll)")]
    public float maxTiltAngle = 12f;

    [Tooltip("Motor-Response-Time in Sekunden (~150ms bei AR-Drone 2.0)")]
    public float responseTime = 0.15f;

    [Header("Hover Damping")]
    [Tooltip("Horizontale Geschwindigkeitsdaempfung im Hover-Modus (0..1 pro Sekunde)")]
    public float hoverDamping = 3.0f;

    [Tooltip("EPS-Schwellwert fuer Auto-Hover (wie _EPS in update_teleop)")]
    public float autoHoverEps = 1e-6f;

    [Header("Takeoff / Landing")]
    [Tooltip("Zielhoehe beim Takeoff in Metern")]
    public float takeoffHeight = 1.0f;

    [Tooltip("Dauer des Takeoff in Sekunden")]
    public float takeoffDuration = 2.5f;

    [Tooltip("Dauer der Landung in Sekunden")]
    public float landingDuration = 2.0f;

    [Header("Battery Simulation")]
    [Tooltip("Batterie-Drain pro Sekunde (Prozent)")]
    public float batteryDrainPerSec = 0.08f;



    // ═══════════════════════ IDroneController ═══════════════════════

    public DroneState State => _state;
    public NavdataPacket Navdata => _navdata;
    public bool IsReady => _state != DroneState.Emergency;

    // ═══════════════════════ Interne Variablen ═══════════════════════

    Rigidbody _rb;
    DroneState _state = DroneState.Landed;
    NavdataPacket _navdata;

    // Aktuelle Kommandos (Unity-nativ)
    float _cmdPitch;   // forward → +pitch (Nase runter = vorwaerts)
    float _cmdRoll;    // left → -roll (links kippen = links fliegen)
    float _cmdGaz;     // up → +gaz
    float _cmdYawRate; // turn → +yaw (positiv = CCW von oben)

    // Gefilterter Zustand (1st-Order Lag fuer Motor-Response)
    float _filteredPitch;
    float _filteredRoll;
    float _filteredGaz;
    float _filteredYawRate;

    // State-Machine Timer
    float _stateTimer;
    float _takeoffStartY;
    float _landingStartY;

    // Batterie
    float _battery = 100f;



    // ═══════════════════════ Unity Lifecycle ═══════════════════════

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _navdata = new NavdataPacket();
        _rb.mass = mass;
        _rb.useGravity = true;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        // Drag wird manuell gesteuert
        _rb.linearDamping = 0.1f;
        _rb.angularDamping = 2.0f;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        switch (_state)
        {
            case DroneState.Landed:
                UpdateLanded(dt);
                break;
            case DroneState.TakingOff:
                UpdateTakingOff(dt);
                break;
            case DroneState.Hovering:
            case DroneState.Flying:
                UpdateFlying(dt);
                break;
            case DroneState.Landing:
                UpdateLanding(dt);
                break;
            case DroneState.Emergency:
                // Motoren aus — Gravity wirkt
                break;
        }

        // Batterie
        if (_state != DroneState.Landed && _state != DroneState.Emergency)
        {
            _battery -= batteryDrainPerSec * dt;
            _battery = Mathf.Max(0f, _battery);
            if (_battery <= 0f)
            {
                _state = DroneState.Emergency;
            }
        }

        // NavData aktualisieren
        UpdateNavdata();
    }

    // ═══════════════════════ State Updates ═══════════════════════

    void UpdateLanded(float dt)
    {
        // Am Boden: Keine Kraft, kein Tilt
        _filteredPitch = _filteredRoll = _filteredGaz = _filteredYawRate = 0f;
    }

    void UpdateTakingOff(float dt)
    {
        _stateTimer += dt;
        float progress = Mathf.Clamp01(_stateTimer / takeoffDuration);

        // Sanft auf takeoffHeight steigen
        float targetY = _takeoffStartY + takeoffHeight * progress;
        float heightError = targetY - transform.position.y;
        float liftForce = _rb.mass * Physics.gravity.magnitude + heightError * 5f;
        _rb.AddForce(Vector3.up * liftForce, ForceMode.Force);

        // Stabilisierung waehrend Takeoff
        StabilizeAttitude(dt, 0f, 0f);

        if (progress >= 1f)
        {
            _state = DroneState.Hovering;
            _stateTimer = 0f;
        }
    }

    void UpdateFlying(float dt)
    {
        // ── 1. Motor-Response: 1st-Order Lag (tau = responseTime) ──
        float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, responseTime));
        _filteredPitch = Mathf.Lerp(_filteredPitch, _cmdPitch, alpha);
        _filteredRoll = Mathf.Lerp(_filteredRoll, _cmdRoll, alpha);
        _filteredGaz = Mathf.Lerp(_filteredGaz, _cmdGaz, alpha);
        _filteredYawRate = Mathf.Lerp(_filteredYawRate, _cmdYawRate, alpha);

        // ── 2. Auto-Hover Check (wie update_teleop: alle < _EPS → Hover) ──
        bool isHoverCmd = Mathf.Abs(_cmdPitch) < autoHoverEps
                       && Mathf.Abs(_cmdRoll) < autoHoverEps
                       && Mathf.Abs(_cmdGaz) < autoHoverEps
                       && Mathf.Abs(_cmdYawRate) < autoHoverEps;

        if (isHoverCmd && _state == DroneState.Flying)
        {
            _state = DroneState.Hovering;
        }
        else if (!isHoverCmd && _state == DroneState.Hovering)
        {
            _state = DroneState.Flying;
        }

        // ── 3. Tilt-basierte Bewegung ──
        // Target Tilt-Winkel aus Befehlen
        float targetPitchDeg = _filteredPitch * maxTiltAngle; // Neigung vorwaerts/rueckwaerts
        float targetRollDeg = _filteredRoll * maxTiltAngle;   // Neigung links/rechts

        // Visuell + physikalisch Attitude anpassen
        StabilizeAttitude(dt, targetPitchDeg, targetRollDeg);

        // ── 4. Horizontalkraft aus Neigung ──
        // Wie bei echtem Quadcopter: Neigung erzeugt laterale Kraftkomponente
        float pitchRad = targetPitchDeg * Mathf.Deg2Rad;
        float rollRad = targetRollDeg * Mathf.Deg2Rad;

        // Kraft in lokalen Koordinaten
        Vector3 localForce = new Vector3(
            Mathf.Sin(rollRad),  // seitlich (Roll)
            0f,
            Mathf.Sin(pitchRad)  // vorwaerts/rueckwaerts (Pitch)
        );

        // In Weltkoordinaten umrechnen (nur Yaw-Rotation, nicht den Tilt!)
        float yawRad = transform.eulerAngles.y * Mathf.Deg2Rad;
        Vector3 worldHForce = new Vector3(
            localForce.x * Mathf.Cos(yawRad) + localForce.z * Mathf.Sin(yawRad),
            0f,
            -localForce.x * Mathf.Sin(yawRad) + localForce.z * Mathf.Cos(yawRad)
        );

        // Kraft skalieren: bei maxTiltAngle → maxHorizontalSpeed erreichen
        float horizontalForceMag = _rb.mass * maxHorizontalSpeed / Mathf.Max(0.1f, responseTime);
        _rb.AddForce(worldHForce * horizontalForceMag, ForceMode.Force);

        // ── 5. Geschwindigkeitslimit horizontal ──
        Vector3 hVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (hVel.magnitude > maxHorizontalSpeed)
        {
            hVel = hVel.normalized * maxHorizontalSpeed;
            _rb.linearVelocity = new Vector3(hVel.x, _rb.linearVelocity.y, hVel.z);
        }

        // ── 6. Vertikale Steuerung (Gaz) ──
        float targetVSpeed = _filteredGaz * maxVerticalSpeed;
        float currentVSpeed = _rb.linearVelocity.y;
        float vSpeedError = targetVSpeed - currentVSpeed;

        // Schwerkraftkompensation + Gaz-Korrektur
        float liftForce = _rb.mass * Physics.gravity.magnitude + vSpeedError * _rb.mass * 5f;
        _rb.AddForce(Vector3.up * liftForce, ForceMode.Force);

        // Vertikales Speed-Limit
        float clampedVy = Mathf.Clamp(_rb.linearVelocity.y, -maxVerticalSpeed, maxVerticalSpeed);
        _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, clampedVy, _rb.linearVelocity.z);

        // ── 7. Yaw-Rotation (Closed-Loop Yaw-Rate Controller) ──
        float targetYawRate = _filteredYawRate * maxYawRate; // Grad/s
        float currentYawRate = _rb.angularVelocity.y * Mathf.Rad2Deg; // Grad/s
        float yawRateError = targetYawRate - currentYawRate;
        float yawTorque = yawRateError * 0.05f * _rb.mass;
        _rb.AddTorque(Vector3.up * yawTorque, ForceMode.Force);

        // ── 8. Hover-Damping (wenn im Hover-Modus) ──
        if (_state == DroneState.Hovering)
        {
            // Geschwindigkeit daempfen
            Vector3 dampForce = -hVel * hoverDamping * _rb.mass;
            _rb.AddForce(dampForce, ForceMode.Force);
        }
    }

    void UpdateLanding(float dt)
    {
        _stateTimer += dt;
        float progress = Mathf.Clamp01(_stateTimer / landingDuration);

        // Zielhoehe: Referenztrajektorie von Starthoehe nach knapp ueber Boden
        float targetY = Mathf.Lerp(_landingStartY, 0.05f, progress);

        // PD-Regler fuer sanfte, stabile Landung
        float heightError = targetY - transform.position.y;
        float verticalVel = _rb.linearVelocity.y;
        float kP_land = 20f;   // Starker P-Anteil fuer praezises Tracking
        float kD_land = 8f;    // D-Anteil daempft Geschwindigkeit

        float liftForce = _rb.mass * Physics.gravity.magnitude
                        + (heightError * kP_land - verticalVel * kD_land) * _rb.mass;
        // Nicht negativ — Drohne soll nicht in den Boden gedrueckt werden
        _rb.AddForce(Vector3.up * Mathf.Max(0f, liftForce), ForceMode.Force);

        // Stabilisierung waehrend Landung
        StabilizeAttitude(dt, 0f, 0f);

        // Horizontal daempfen
        Vector3 hVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        _rb.AddForce(-hVel * hoverDamping * _rb.mass, ForceMode.Force);

        // Nur landen wenn tatsaechlich nahe am Boden UND langsam genug
        if (transform.position.y < 0.15f && Mathf.Abs(verticalVel) < 0.5f)
        {
            _state = DroneState.Landed;
            _stateTimer = 0f;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
        }
    }

    // ═══════════════════════ Attitude Stabilisierung ═══════════════════════

    /// <summary>
    /// Korrigiert Pitch/Roll auf die Zielwerte via Torques.
    /// Bei targetPitch=0, targetRoll=0 → stabilisiert die Drohne horizontal.
    /// </summary>
    void StabilizeAttitude(float dt, float targetPitchDeg, float targetRollDeg)
    {
        // ── Yaw-Anteil entfernen → reiner Tilt im lokalen Frame ──
        // So bleiben Pitch/Roll-Korrekturen korrekt, egal wie die Drohne geyawt ist.
        Quaternion yawOnly = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        Quaternion tiltRotation = Quaternion.Inverse(yawOnly) * transform.rotation;
        Vector3 tiltEuler = tiltRotation.eulerAngles;

        float currentPitch = tiltEuler.x > 180f ? tiltEuler.x - 360f : tiltEuler.x;
        float currentRoll = tiltEuler.z > 180f ? tiltEuler.z - 360f : tiltEuler.z;

        // PD-Regler fuer Attitude
        float pitchError = targetPitchDeg - currentPitch;
        float rollError = targetRollDeg - currentRoll;

        float kP = 1.2f;
        float kD = 1.0f;

        // Lokale Winkelgeschwindigkeit fuer korrekte Daempfung
        Vector3 localAngVel = transform.InverseTransformDirection(_rb.angularVelocity);

        float pitchTorque = (pitchError * kP - localAngVel.x * kD) * _rb.mass;
        float rollTorque = (rollError * kP - localAngVel.z * kD) * _rb.mass;

        // Torques im lokalen Frame anwenden (korrekt bei jedem Yaw-Winkel)
        _rb.AddRelativeTorque(new Vector3(pitchTorque, 0f, rollTorque), ForceMode.Force);
    }

    // ═══════════════════════ NavData Update ═══════════════════════

    void UpdateNavdata()
    {
        Vector3 vel = _rb.linearVelocity;
        Vector3 euler = transform.eulerAngles;

        // Body-frame Geschwindigkeit (relativ zur Drohne)
        Vector3 localVel = transform.InverseTransformDirection(vel);

        _navdata.altitude = Mathf.Max(0f, transform.position.y);
        _navdata.vx = localVel.z;  // vorwaerts
        _navdata.vy = localVel.x;  // seitlich
        _navdata.vz = vel.y;       // vertikal (world)

        _navdata.rotX = euler.z > 180f ? euler.z - 360f : euler.z; // Roll
        _navdata.rotY = euler.x > 180f ? euler.x - 360f : euler.x; // Pitch
        _navdata.rotZ = euler.y;                                     // Yaw

        // Beschleunigung (vereinfacht)
        _navdata.ax = 0f; // TODO: aus Velocity-Delta berechnen
        _navdata.ay = 0f;
        _navdata.az = 0f;

        _navdata.battery = _battery;
        _navdata.state = (int)_state;
        _navdata.wifiSignal = 100; // Sim: immer perfekt
        _navdata.pressure = (int)(101325f - _navdata.altitude * 12f); // Barometrisch
        _navdata.temperature = 25f; // Sim: Raumtemperatur
        _navdata.timestamp = Time.time;
    }

    // ═══════════════════════ IDroneController Implementierung ═══════════════════════

    public void Takeoff()
    {
        if (_state != DroneState.Landed) return;

        _state = DroneState.TakingOff;
        _stateTimer = 0f;
        _takeoffStartY = transform.position.y;
    }

    public void Land()
    {
        if (_state != DroneState.Hovering && _state != DroneState.Flying) return;

        _state = DroneState.Landing;
        _stateTimer = 0f;
        _landingStartY = transform.position.y;

        // Befehle zuruecksetzen
        _cmdPitch = _cmdRoll = _cmdGaz = _cmdYawRate = 0f;
        _filteredPitch = _filteredRoll = _filteredGaz = _filteredYawRate = 0f;
    }

    public void EmergencyReset()
    {
        if (_state == DroneState.Emergency)
        {
            // Emergency aufheben → Landed
            _state = DroneState.Landed;
            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _battery = 100f;
        }
        else
        {
            // Emergency aktivieren → Motoren aus
            _state = DroneState.Emergency;
            _cmdPitch = _cmdRoll = _cmdGaz = _cmdYawRate = 0f;
            _filteredPitch = _filteredRoll = _filteredGaz = _filteredYawRate = 0f;
        }
    }

    public void Move(float forward, float left, float up, float turn)
    {
        if (_state != DroneState.Hovering && _state != DroneState.Flying) return;

        // ── Vorzeichen-Konvention (Unity-nativ) ──
        _cmdPitch = Mathf.Clamp(forward, -1f, 1f);      // forward → +pitch (Nase runter = vorwaerts)
        _cmdRoll = Mathf.Clamp(-left, -1f, 1f);         // left negiert → -roll (links kippen = links)
        _cmdGaz = Mathf.Clamp(up, -1f, 1f);              // up → +gaz
        _cmdYawRate = Mathf.Clamp(turn, -1f, 1f);       // turn → +yaw (CCW von oben)

        // State-Wechsel Hovering → Flying (wenn Kommando != 0)
        if (_state == DroneState.Hovering)
        {
            bool hasInput = Mathf.Abs(forward) > autoHoverEps
                         || Mathf.Abs(left) > autoHoverEps
                         || Mathf.Abs(up) > autoHoverEps
                         || Mathf.Abs(turn) > autoHoverEps;
            if (hasInput) _state = DroneState.Flying;
        }
    }

    public void Hover()
    {
        _cmdPitch = 0f;
        _cmdRoll = 0f;
        _cmdGaz = 0f;
        _cmdYawRate = 0f;
        // State → Hovering (wenn nicht Landed/TakingOff/Landing/Emergency)
        if (_state == DroneState.Flying) _state = DroneState.Hovering;
    }



    // ═══════════════════════ Oeffentliche Hilfsmethoden ═══════════════════════

    /// <summary>
    /// Setzt den Controller komplett zurueck (fuer Episode-Reset des ML-Agents).
    /// </summary>
    public void ResetController(Vector3 position, Quaternion rotation)
    {
        _state = DroneState.Landed;
        _stateTimer = 0f;
        _battery = 100f;

        _cmdPitch = _cmdRoll = _cmdGaz = _cmdYawRate = 0f;
        _filteredPitch = _filteredRoll = _filteredGaz = _filteredYawRate = 0f;

        _rb.linearVelocity = Vector3.zero;
        _rb.angularVelocity = Vector3.zero;
        transform.position = position;
        transform.rotation = rotation;

        UpdateNavdata();
    }

    /// <summary>Reset + automatischer Takeoff (Convenience fuer Training).</summary>
    public void ResetAndTakeoff(Vector3 position, Quaternion rotation)
    {
        ResetController(position, rotation);
        Takeoff();
    }


}
