using UnityEngine;

/// <summary>
/// ScriptableObject fuer die Bridge-Konfiguration.
/// Erstellen via: Assets > Create > DroneDetect > Bridge Config
///
/// Enthaelt alle Base-URLs, Polling-Raten, Koordinaten und Stabilitaetsparameter.
/// Wird von DroneBridgeService, SensorAdapter und FusionMapRenderer referenziert.
/// </summary>
[CreateAssetMenu(fileName = "DroneBridgeConfig", menuName = "DroneDetect/Bridge Config")]
public class DroneBridgeConfig : ScriptableObject
{
    // ──────────────── API Base URLs ────────────────

    [Header("API Endpoints")]
    [Tooltip("Simulator API (Bild, NavData, Status)")]
    public string simBase = "http://localhost:5051";

    [Tooltip("WiFi/CSI-Sensing Service")]
    public string wifiBase = "http://localhost:6060";

    [Tooltip("Depth/Pointcloud Service")]
    public string depthBase = "http://localhost:6061";

    [Tooltip("OSM Map Service")]
    public string osmBase = "http://localhost:6062";

    [Tooltip("Fusion Service (Fuse + Map + Anomalies)")]
    public string fusionBase = "http://localhost:6063";

    // ──────────────── Drone Identity ────────────────

    [Header("Drone")]
    public string droneId = "drone_001";

    // ──────────────── Polling / Loop Rates ────────────────

    [Header("Loop Rates (Hz)")]
    [Tooltip("Sensor-Upload (NavData + Image + WiFi + Depth)")]
    [Range(0.5f, 20f)]
    public float sensorHz = 5f;

    [Tooltip("Fusion-Trigger (/fuse)")]
    [Range(0.2f, 5f)]
    public float fusionHz = 1.5f;

    [Tooltip("Map + Anomalies Pull")]
    [Range(0.2f, 5f)]
    public float mapHz = 1f;

    [Tooltip("Health-Check aller Services")]
    [Range(0.05f, 1f)]
    public float healthHz = 0.2f;

    // ──────────────── OSM Init ────────────────

    [Header("OSM Init (einmal pro Episode)")]
    public float initLat = 52.52f;
    public float initLon = 13.41f;
    public int initRadius = 100;

    // ──────────────── Stability / Timeouts ────────────────

    [Header("Stability")]
    [Tooltip("HTTP-Timeout pro Request in Sekunden")]
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
