using UnityEngine;

/// <summary>
/// ObjectDetectionReward — Belohnungssystem fuer Kamera-basierte Erkennung.
///
/// Zwei Modi:
///   1. CheckDetection(target): Navigations-Targets — Approach-Bonus + FOV-Sichtbarkeit
///   2. CheckObservation(target): Beobachtungstarget — kleiner Reward fuers im-Blick-halten
///
/// Kein Raycast — die GroundCam ist eine Bodenkamera (Ultraschall/Optischer Fluss),
/// "freie Sicht" macht da keinen Sinn. Stattdessen: Viewport + Distanz direkt.
/// </summary>
public class ObjectDetectionReward : MonoBehaviour
{
    [Header("Detection Settings")]
    [Tooltip("Bodenkamera der Drohne")]
    public Camera groundCam;

    [Tooltip("Maximale Erkennungsdistanz in Metern")]
    public float detectionRange = 20f;

    [Header("Observation Target (Bodenkamera)")]
    [Tooltip("Reward-Gewicht fuers Beobachtungstarget im Blickfeld halten (0.0 - 0.1)")]
    public float observationRewardWeight = 0.05f;

    float _prevDist = float.MaxValue;

    /// <summary>
    /// Setzt die vorherige Distanz zurueck (bei Episode-Start aufrufen).
    /// </summary>
    public void ResetTracking()
    {
        _prevDist = float.MaxValue;
    }

    /// <summary>
    /// Navigations-Target: Approach-Bonus + Sichtbarkeits-Reward.
    /// Agent soll aktiv hinfliegen und einsammeln.
    /// </summary>
    public float CheckDetection(Transform target)
    {
        if (groundCam == null || target == null) return 0f;

        Vector3 delta = target.position - groundCam.transform.position;
        // Y (Hoehe) staerker gewichten als X/Z
        float dist = Mathf.Sqrt(delta.x * delta.x + delta.z * delta.z + delta.y * delta.y * 4f);

        if (dist > detectionRange)
        {
            _prevDist = dist;
            return 0f;
        }

        float reward = 0f;

        // Approach-Bonus: Belohnung fuer jedes Stueck Annaeherung
        if (_prevDist < float.MaxValue)
        {
            float closingDist = _prevDist - dist;
            if (closingDist > 0f)
                reward += closingDist * 0.1f;
        }
        _prevDist = dist;

        // Sichtbarkeits-Reward: ist Target im Kamera-FOV?
        float fovReward = GetFovReward(target, dist);
        reward += fovReward;

        return reward;
    }

    /// <summary>
    /// Beobachtungstarget: kleiner Reward nur fuers im-Blickfeld-halten.
    /// Kein Approach-Bonus, kein Einsammeln — rein visuelles Tracking.
    /// </summary>
    public float CheckObservation(Transform target)
    {
        if (groundCam == null || target == null) return 0f;

        Vector3 viewportPos = groundCam.WorldToViewportPoint(target.position);

        bool inView = viewportPos.z > 0
                    && viewportPos.x > 0.05f && viewportPos.x < 0.95f
                    && viewportPos.y > 0.05f && viewportPos.y < 0.95f;

        if (!inView) return 0f;

        // Zentrierung im Bild
        float centerX = Mathf.Abs(viewportPos.x - 0.5f);
        float centerY = Mathf.Abs(viewportPos.y - 0.5f);
        float centerScore = 1f - (centerX + centerY); // 0..1

        return centerScore * observationRewardWeight;
    }

    /// <summary>
    /// Interner FOV-Check + Proximity-skalierter Sichtbarkeits-Reward.
    /// </summary>
    float GetFovReward(Transform target, float dist)
    {
        Vector3 viewportPos = groundCam.WorldToViewportPoint(target.position);

        bool inView = viewportPos.z > 0
                    && viewportPos.x > 0.1f && viewportPos.x < 0.9f
                    && viewportPos.y > 0.1f && viewportPos.y < 0.9f;

        if (!inView) return 0f;

        // Zentrierung im Bild
        float centerX = Mathf.Abs(viewportPos.x - 0.5f);
        float centerY = Mathf.Abs(viewportPos.y - 0.5f);
        float centerScore = 1f - (centerX + centerY);

        // Naeher = mehr Reward
        float proximityScale = 1f - Mathf.Clamp01(dist / detectionRange);
        return centerScore * proximityScale * 0.3f;
    }
}
