using UnityEngine;
using System.Collections;

/// <summary>
/// Rotor — Visuelle Rotor-Animation fuer den AR-Drone 2.0 Digital Twin.
///
/// Liest den Drohnen-State aus SimulatedDroneController und passt
/// die Rotorgeschwindigkeit automatisch an:
///   Landed/Emergency → Rotoren stehen
///   TakingOff/Landing → Rotoren drehen mit halber Geschwindigkeit
///   Hovering → Rotoren drehen mit Grundgeschwindigkeit
///   Flying → Rotoren drehen proportional zur Motorlast
///
/// WICHTIG: Keine AddForce mehr — alle Physik laeuft ueber SimulatedDroneController.
/// </summary>
public class rotor : MonoBehaviour
{
    /// <summary>
    /// Drehrichtung des Rotors (im Inspector setzen).
    /// </summary>
    public bool counterclockwise = false;

    /// <summary>
    /// Visuelle Animation ein/aus.
    /// </summary>
    public bool animationActivated = true;

    [Tooltip("Basis-Drehgeschwindigkeit (Grad/s bei Hover)")]
    public float baseSpinSpeed = 2000f;

    [Tooltip("Max Drehgeschwindigkeit (Grad/s bei Vollgas)")]
    public float maxSpinSpeed = 4000f;

    SimulatedDroneController _controller;
    float _currentSpeed;

    void Start()
    {
        _controller = GetComponentInParent<SimulatedDroneController>();
        if (_controller == null)
            Debug.LogWarning($"[rotor] {name}: Kein SimulatedDroneController im Parent gefunden!");
    }

    void Update()
    {
        if (!animationActivated || _controller == null) return;

        // Ziel-Drehgeschwindigkeit abhaengig vom Drone-State
        float targetSpeed = 0f;

        switch (_controller.State)
        {
            case DroneState.Landed:
            case DroneState.Emergency:
                targetSpeed = 0f;
                break;

            case DroneState.TakingOff:
            case DroneState.Landing:
                targetSpeed = baseSpinSpeed * 0.6f;
                break;

            case DroneState.Hovering:
                targetSpeed = baseSpinSpeed;
                break;

            case DroneState.Flying:
                // Schnellere Rotation bei aktivem Flug
                targetSpeed = Mathf.Lerp(baseSpinSpeed, maxSpinSpeed, 0.5f);
                break;
        }

        // Sanftes An-/Auslaufen (Motor-Lag visuell simuliert)
        _currentSpeed = Mathf.Lerp(_currentSpeed, targetSpeed, Time.deltaTime * 8f);

        float direction = counterclockwise ? -1f : 1f;
        transform.Rotate(0, 0, _currentSpeed * direction * Time.deltaTime);
    }
}
