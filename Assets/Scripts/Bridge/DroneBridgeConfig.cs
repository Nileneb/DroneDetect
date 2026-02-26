using UnityEngine;

/// <summary>
/// ScriptableObject fuer die Bridge-Konfiguration.
/// Erstellen via: Assets > Create > DroneDetect > Bridge Config
///
/// Enthaelt alle Base-URLs, Polling-Raten, Koordinaten und Stabilitaetsparameter.
/// Wird von DroneBridgeService, SensorAdapter und FusionMapRenderer referenziert.
///
/// Hinweis: Depth-API ist deaktiviert (zu langsam).
/// Stattdessen: RGB Image Features + WiFi CSI → schnellere Verarbeitung.
/// </summary>
[CreateAssetMenu(fileName = "DroneBridgeConfig", menuName = "DroneDetect/Bridge Config")]
public class DroneBridgeConfig : ScriptableObject
{
    // ──────────────── API Base URLs ────────────────

    [Header("API Endpoints")]
    [Tooltip("Simulator API (Bild, NavData, Status)")]
    public string simBase = "http://localhost:5051";

    [Tooltip("WiFi/CSI-Sensing Service (WiFi CSI + RGB Features)")]
    public string wifiBase = "http://localhost:6060";

    [Tooltip("OSM Map Service (2D Landmarks)")]
    public string osmBase = "http://localhost:6062";

    [Tooltip("Fusion Service (Fuse + Map + Anomalies)")]
    public string fusionBase = "http://localhost:6063";

    // ──────────────── Drone Identity ────────────────

    [Header("Drone")]
    public string droneId = "drone_001";

    // ──────────────── Polling / Loop Rates ────────────────

    [Header("Loop Rates (Hz)")]
    [Tooltip("Sensor-Upload (WiFi CSI + RGB Features, 5 Hz = 200ms)")]
    [Range(0.5f, 20f)]
    public float sensorHz = 5f;

    [Tooltip("Fusion-Trigger (/fuse, 1-2 Hz)")]
    [Range(0.2f, 5f)]
    public float fusionHz = 1.5f;

    [Tooltip("Map + Anomalies Pull (1-2 Hz)")]
    [Range(0.2f, 5f)]
    public float mapHz = 1f;

    [Tooltip("Health-Check aller Services (0.2 Hz = alle 5s)")]
    [Range(0.05f, 1f)]
    public float healthHz = 0.2f;

    // ──────────────── OSM Init ────────────────

    [Header("OSM Init (einmal pro Episode)")]
    public float initLat = 52.52f;
    public float initLon = 13.41f;
    public int initRadius = 100;

    // ──────────────── Stability / Timeouts ────────────────

    [Header("Stability")]
    [Tooltip("HTTP-Timeout pro Request in Sekunden (2-3s empfohlen)")]
    [Range(1, 10)]
    public int httpTimeoutSec = 3;

    [Tooltip("Max. Wartezeit beim Service-Boot in Sekunden")]
    [Range(5, 120)]
    public float serviceBootTimeoutSec = 30f;

    [Tooltip("Fusion-Latenz-Schwelle (Sekunden) ab der Sensor-Uploads gedrosselt werden")]
    [Range(0.5f, 5f)]
    public float fusionLatencyThrottleSec = 1f;

    [Tooltip("Maximale Punkte beim Map-Pull")]
    public int maxMapPoints = 5000;

    // ──────────────── Synthetic CSI ────────────────

    [Header("Synthetic CSI")]
    [Tooltip("Anzahl Subcarrier-Reihen fuer synthetische Amplitude/Phase")]
    public int syntheticSubcarriers = 30;

    [Tooltip("Zeitschritte pro Subcarrier")]
    public int syntheticTimeSteps = 20;

    // ──────────────── Convenience ────────────────

    /// <summary>Interval in Sekunden fuer den Sensor-Loop.</summary>
    public float SensorInterval => 1f / Mathf.Max(0.5f, sensorHz);

    /// <summary>Interval in Sekunden fuer den Fusion-Trigger-Loop.</summary>
    public float FusionInterval => 1f / Mathf.Max(0.2f, fusionHz);

    /// <summary>Interval in Sekunden fuer den Map-Pull-Loop.</summary>
    public float MapInterval => 1f / Mathf.Max(0.2f, mapHz);

    /// <summary>Interval in Sekunden fuer den Health-Check-Loop.</summary>
    public float HealthInterval => 1f / Mathf.Max(0.05f, healthHz);
}
