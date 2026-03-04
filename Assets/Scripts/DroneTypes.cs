using System;
using UnityEngine;

// ═══════════════════════════════════════════════════════════════
// DroneTypes.cs — Gemeinsame Datentypen fuer das Digital-Twin-System
//
// DroneState   : State-Machine Zustaende (wie ardrone_autonomy)
// NavdataPacket: Einheitliches Navdata-Format (Sim + Real identisch)
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// State-Machine der AR-Drone 2.0 (aus ardrone_autonomy).
/// Bildet die Zustaende 1:1 ab.
/// </summary>
public enum DroneState
{
    /// <summary>Drohne steht am Boden, Motoren aus.</summary>
    Landed = 0,

    /// <summary>Drohne hebt gerade ab.</summary>
    TakingOff = 1,

    /// <summary>Drohne schwebt stabil (Auto-Hover aktiv).</summary>
    Hovering = 2,

    /// <summary>Drohne fliegt aktiv (Bewegungsbefehle).</summary>
    Flying = 3,

    /// <summary>Drohne landet gerade.</summary>
    Landing = 4,

    /// <summary>Notfall-Zustand (Motoren gestoppt).</summary>
    Emergency = 5
}

/// <summary>
/// Einheitliches NavData-Paket — wird von IDroneController.Navdata geliefert.
/// Gleiche Felder egal ob Simulation oder echte Drohne.
///
/// Einheiten (wie ardrone_autonomy):
///   altitude  : Meter
///   vx/vy/vz  : m/s
///   rotX/Y/Z  : Grad (Euler)
///   battery   : 0..100 Prozent
/// </summary>
[Serializable]
public class NavdataPacket
{
    // ── Position / Hoehe ──
    /// <summary>Hoehe ueber Grund in Metern.</summary>
    public float altitude;

    // ── Geschwindigkeit (Body-Frame) ──
    /// <summary>Vorwaerts-Geschwindigkeit in m/s (positiv = vorwaerts).</summary>
    public float vx;
    /// <summary>Seitliche Geschwindigkeit in m/s (positiv = rechts).</summary>
    public float vy;
    /// <summary>Vertikale Geschwindigkeit in m/s (positiv = hoch).</summary>
    public float vz;

    // ── Rotation (Euler, Grad) ──
    /// <summary>Roll in Grad.</summary>
    public float rotX;
    /// <summary>Pitch in Grad.</summary>
    public float rotY;
    /// <summary>Yaw in Grad.</summary>
    public float rotZ;

    // ── Beschleunigung ──
    /// <summary>Beschleunigung X in m/s².</summary>
    public float ax;
    /// <summary>Beschleunigung Y in m/s².</summary>
    public float ay;
    /// <summary>Beschleunigung Z in m/s².</summary>
    public float az;

    // ── System ──
    /// <summary>Batterie-Stand in Prozent (0..100).</summary>
    public float battery;

    /// <summary>Aktueller DroneState als int (fuer Kompatibilitaet mit API).</summary>
    public int state;

    /// <summary>WiFi-Signalstaerke (0..100).</summary>
    public int wifiSignal;

    /// <summary>Luftdruck in Pascal.</summary>
    public int pressure;

    /// <summary>Temperatur in Celsius.</summary>
    public float temperature;

    // ── Zeitstempel ──
    /// <summary>Unity-Zeit bei Erstellung dieses Pakets.</summary>
    public float timestamp;

    /// <summary>Erstellt ein leeres NavdataPacket mit Defaults.</summary>
    public NavdataPacket()
    {
        battery = 100f;
        timestamp = 0f;
    }

    /// <summary>Typed State (Convenience).</summary>
    public DroneState DroneState
    {
        get => (DroneState)state;
        set => state = (int)value;
    }
}
