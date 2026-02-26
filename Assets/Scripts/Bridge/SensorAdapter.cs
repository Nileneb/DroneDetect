using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// SensorAdapter — Wandelt Simulator-Roh-Daten (NavData, Image, Quality, CSI)
/// in die JSON-Payloads um, die die Micro-Services erwarten.
///
/// Verantwortlichkeiten:
///   - Koordinaten-Mapping: Unity (x,y,z) -> API local_enu (x=x, y=z, z=y)
///   - Yaw in Radiant
///   - Synthetische CSI-Amplitude/Phase aus wifi_signal + Rauschen
///   - ImageFeatures-Payload aus Simulator /image/quality
///   - Depth-Payload (base64-JPEG)
///
/// Kein eigener HTTP — wird von DroneBridgeService aufgerufen.
/// </summary>
public static class SensorAdapter
{
    // ════════════ Data Transfer Objects (Payloads) ════════════

    // --- Uplink Payloads ---

    [Serializable]
    public class PositionPayload
    {
        public float x;
        public float y;
        public float z;
        public float yaw;
    }

    [Serializable]
    public class FusionTriggerPayload
    {
        public string drone_id;
        public PositionPayload position;
    }

    [Serializable]
    public class DepthPayload
    {
        public string image;
    }

    [Serializable]
    public class ImageFeatures
    {
        public float edge_density;
        public float blur_level;
        public float noise_level;
        public float compression_ratio;
    }

    [Serializable]
    public class LatLonPosition
    {
        public float lat;
        public float lon;
        public float alt;
    }

    [Serializable]
    public class WifiAnalyzePayload
    {
        public List<List<float>> amplitude;
        public List<List<float>> phase;
        public ImageFeatures image_features;
        public LatLonPosition position;
        public double timestamp;
    }

    [Serializable]
    public class OsmInitPayload
    {
        public float lat;
        public float lon;
        public int radius;
    }

    // --- Downlink Response Models ---

    [Serializable]
    public class NavDataResponse
    {
        public float altitude;
        public float battery;
        public float vx, vy, vz;
        public float rotX, rotY, rotZ;
        public float ax, ay, az;
        public int state;
        public int wifi_signal;
        public int pressure;
        public float temperature;
    }

    [Serializable]
    public class ImageResponse
    {
        public string data;
        public int width;
        public int height;
        public string format;
    }

    [Serializable]
    public class ImageQualityResponse
    {
        public float edge_density;
        public float blur_level;
        public float noise_level;
        public float brightness;
        public float compression_ratio;
    }

    [Serializable]
    public class FusionPoint
    {
        public float[] position;
        public string label;
        public string[] sources;
        public bool is_anomaly;
    }

    [Serializable]
    public class FusionMapResponse
    {
        public int n_voxels;
        public List<FusionPoint> points;
    }

    [Serializable]
    public class AnomalyEntry
    {
        public float[] position;
        public string label;
        public float score;
    }

    [Serializable]
    public class AnomalyListResponse
    {
        public List<AnomalyEntry> anomalies;
    }

    [Serializable]
    public class MapStatsResponse
    {
        public int total_voxels;
        public int anomaly_count;
        public string[] sources;
    }

    // --- Drone Control Models (ehemals in DroneAPIBridge) ---

    [Serializable]
    public class StatusResponse
    {
        public bool connected, flying, emergency, camera_ready;
        public float battery;
    }

    [Serializable]
    public class MoveCommand
    {
        public float forward, right, up, turn_speed;
    }

    // ════════════ Coordinate Mapping ════════════

    /// <summary>
    /// Unity (x,y,z) -> API local_enu (x,y,z).
    /// api_x = unity_x, api_y = unity_z, api_z = unity_y (Hoehe)
    /// </summary>
    public static PositionPayload UnityToEnu(Vector3 unityPos, float yawDegrees)
    {
        return new PositionPayload
        {
            x = unityPos.x,
            y = unityPos.z,
            z = unityPos.y,
            yaw = yawDegrees * Mathf.Deg2Rad
        };
    }

    /// <summary>
    /// API local_enu (x,y,z) -> Unity (x,y,z).
    /// unity_x = api_x, unity_y = api_z, unity_z = api_y
    /// </summary>
    public static Vector3 EnuToUnity(float[] enuPos)
    {
        if (enuPos == null || enuPos.Length < 3) return Vector3.zero;
        return new Vector3(enuPos[0], enuPos[2], enuPos[1]);
    }

    // ════════════ Payload Builders ════════════

    /// <summary>Baut das OSM-Init-Payload.</summary>
    public static OsmInitPayload BuildOsmInit(DroneBridgeConfig cfg)
    {
        return new OsmInitPayload
        {
            lat = cfg.initLat,
            lon = cfg.initLon,
            radius = cfg.initRadius
        };
    }

    /// <summary>
    /// Baut den Fusion-Trigger-Payload aus NavData oder Unity-Transform.
    /// </summary>
    public static FusionTriggerPayload BuildFusionTrigger(
        DroneBridgeConfig cfg,
        NavDataResponse nav,
        Vector3 unityPosition,
        float yawDegrees)
    {
        // Bevorzuge Unity-Position (genauer als API-NavData im Sim-Modus)
        var pos = UnityToEnu(unityPosition, yawDegrees);

        // Hoehe: Wenn NavData vorhanden, Altitude in Meter (API liefert mm)
        if (nav != null)
            pos.z = nav.altitude / 1000f;

        return new FusionTriggerPayload
        {
            drone_id = cfg.droneId,
            position = pos
        };
    }

    /// <summary>Baut den Depth-Payload aus base64-Image.</summary>
    public static DepthPayload BuildDepthPayload(string imageBase64)
    {
        return new DepthPayload { image = imageBase64 };
    }

    /// <summary>
    /// Baut den WiFi-Analyze-Payload mit synthetischem CSI und Bildqualitaet.
    /// </summary>
    public static WifiAnalyzePayload BuildWifiAnalyze(
        DroneBridgeConfig cfg,
        NavDataResponse nav,
        ImageQualityResponse quality)
    {
        float wifiSignal = nav != null ? nav.wifi_signal : 0f;
        float alt = nav != null ? nav.altitude / 1000f : 0f;

        return new WifiAnalyzePayload
        {
            amplitude = BuildSyntheticAmplitude(wifiSignal, cfg.syntheticSubcarriers, cfg.syntheticTimeSteps),
            phase = BuildSyntheticPhase(cfg.syntheticSubcarriers, cfg.syntheticTimeSteps),
            image_features = new ImageFeatures
            {
                edge_density = quality != null ? quality.edge_density : 0.1f,
                blur_level = quality != null ? quality.blur_level : 100f,
                noise_level = quality != null ? quality.noise_level : 5f,
                compression_ratio = quality != null ? quality.compression_ratio : 0.5f
            },
            position = new LatLonPosition
            {
                lat = cfg.initLat,
                lon = cfg.initLon,
                alt = alt
            },
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0
        };
    }

    // ════════════ Synthetic CSI ════════════

    /// <summary>
    /// Erzeugt synthetische CSI-Amplituden aus wifi_signal + Rauschen.
    /// Wird fuer Training genutzt; echte CSI kommt spaeter aus Hardware-Bridge.
    /// </summary>
    public static List<List<float>> BuildSyntheticAmplitude(float wifiSignal, int subcarriers, int timeSteps)
    {
        var result = new List<List<float>>(subcarriers);
        float baseValue = Mathf.Clamp01((wifiSignal + 100f) / 100f) + 0.1f;

        for (int sc = 0; sc < subcarriers; sc++)
        {
            var row = new List<float>(timeSteps);
            for (int t = 0; t < timeSteps; t++)
            {
                float noise = UnityEngine.Random.Range(-0.05f, 0.05f);
                row.Add(Mathf.Max(0.01f, baseValue + noise + sc * 0.002f));
            }
            result.Add(row);
        }

        return result;
    }

    /// <summary>Erzeugt synthetische CSI-Phasen (gleichverteilt [-pi, pi]).</summary>
    public static List<List<float>> BuildSyntheticPhase(int subcarriers, int timeSteps)
    {
        var result = new List<List<float>>(subcarriers);
        for (int sc = 0; sc < subcarriers; sc++)
        {
            var row = new List<float>(timeSteps);
            for (int t = 0; t < timeSteps; t++)
            {
                row.Add(UnityEngine.Random.Range(-Mathf.PI, Mathf.PI));
            }
            result.Add(row);
        }
        return result;
    }
}
