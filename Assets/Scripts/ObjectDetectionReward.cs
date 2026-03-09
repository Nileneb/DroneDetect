using UnityEngine;

/// <summary>
/// ObjectDetectionReward — Gibt Reward wenn ein Zielobjekt im Kamera-Sichtfeld
/// zentriert erkannt wird. Reward wird mit Naeherung skaliert, damit der Agent
/// nicht einfach schwebt, sondern aktiv auf Targets zufliegt und einsammelt.
/// </summary>
public class ObjectDetectionReward : MonoBehaviour
{
    [Header("Detection Settings")]
    [Tooltip("Kamera die nach unten / vorne schaut (Ground-Cam oder Front-Cam)")]
    public Camera groundCam;

    [Tooltip("Layer fuer erkennbare Objekte")]
    public LayerMask detectableObjects;

    [Tooltip("Maximale Erkennungsdistanz in Metern")]
    public float detectionRange = 20f;

    float _prevDist = float.MaxValue;

    /// <summary>
    /// Setzt die vorherige Distanz zurueck (bei Episode-Start aufrufen).
    /// </summary>
    public void ResetTracking()
    {
        _prevDist = float.MaxValue;
    }

    /// <summary>
    /// Prueft Sichtbarkeit + Zentrierung eines Targets.
    /// Gibt kombinierten Reward zurueck:
    ///   - Sichtbarkeits-Bonus (skaliert mit Naehe)
    ///   - Approach-Bonus (wenn Agent sich dem Target naehert)
    /// </summary>
    public float CheckDetection(Transform target)
    {
        if (groundCam == null || target == null) return 0f;

        Vector3 dirToTarget = target.position - groundCam.transform.position;
        // Y (Hoehe) staerker gewichten als X/Z
        float dist = Mathf.Sqrt(dirToTarget.x * dirToTarget.x + dirToTarget.z * dirToTarget.z + dirToTarget.y * dirToTarget.y * 4f);

        if (dist > detectionRange)
        {
            _prevDist = dist;
            return 0f;
        }

        float reward = 0f;

        // ── Approach-Bonus: Belohnung fuer Annaeherung ──
        if (_prevDist < float.MaxValue)
        {
            float closingDist = _prevDist - dist;
            if (closingDist > 0f)
                reward += closingDist * 0.1f;  // Bonus fuer jedes naeher gekommene Meter
        }
        _prevDist = dist;

        // ── Sichtbarkeits-Reward (nur wenn im Kamera-FOV) ──
        Vector3 viewportPos = groundCam.WorldToViewportPoint(target.position);
        bool inView = viewportPos.z > 0
                    && viewportPos.x > 0.1f && viewportPos.x < 0.9f
                    && viewportPos.y > 0.1f && viewportPos.y < 0.9f;

        if (!inView) return reward;

        // Raycast: freie Sicht?
        if (Physics.Raycast(groundCam.transform.position, dirToTarget.normalized,
                           out RaycastHit hit, dist, detectableObjects))
        {
            if (hit.transform == target)
            {
                // Zentrierung im Bild
                float centerX = Mathf.Abs(viewportPos.x - 0.5f);
                float centerY = Mathf.Abs(viewportPos.y - 0.5f);
                float centerScore = 1f - (centerX + centerY);

                // Naeher = mehr Reward (Proximity-Skalierung)
                float proximityScale = 1f - Mathf.Clamp01(dist / detectionRange);
                reward += centerScore * proximityScale * 0.3f;
            }
        }
        return reward;
    }
}
