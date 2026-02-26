using System;
using UnityEngine;

/// <summary>
/// Tracks drone progress along a Waypoint route (Circuit or SinglePoint).
/// 
/// TWO MODES:
///   Classic  – steuert den droneMovementController direkt (Original-Verhalten)
///   ReadOnly – liefert nur Metriken fuer den ML-Agent (Rewards / Observations)
///
/// PUBLIC METRIKEN (fuer DroneMLAgent):
///   ProgressDistance        – zurueckgelegter Weg auf der Route
///   DeviationFromRoute     – aktuelle Abweichung von der Route (Meter)
///   NormalizedProgress     – Fortschritt 0..1 (nur bei Circuit mit bekannter Laenge)
///   ProgressDelta          – Fortschritts-Aenderung seit letztem Frame
///   RouteDirection         – Soll-Flugrichtung an der aktuellen Position
///   NearestRoutePoint      – naechster Punkt auf der Route
///   HasWaypoint            – ist eine Route zugewiesen?
/// </summary>
public class WaypointProgressTracker : MonoBehaviour
{
    // ──────────── Configuration ────────────
    [SerializeField] private Waypoint waypoint;

    [Header("Mode")]
    [Tooltip("ReadOnly = nur Metriken fuer ML-Agent, Classic = steuert droneMovementController")]
    public bool readOnlyMode = true;

    [Header("Circuit Settings")]
    [SerializeField] private float lookAheadForTargetOffset = 5;
    [SerializeField] private float maxDistFromCircuit = 1;
    [SerializeField] private float distanceOfPointToLookAt = 8;

    [Header("Target (nur Classic-Mode oder Gizmo)")]
    public Transform target;

    // ──────────── Public Metrics ────────────
    /// <summary>Absolute distance traveled along the route</summary>
    public float ProgressDistance => progressDistance;

    /// <summary>Progress delta since last FixedUpdate (positive = forward)</summary>
    public float ProgressDelta => progressDeltaPerFrame;

    /// <summary>Distance from the drone to the nearest route point (meters)</summary>
    public float DeviationFromRoute => deviationFromRoute;

    /// <summary>Normalized progress [0..1] for circuits (0 if single point or unknown length)</summary>
    public float NormalizedProgress => normalizedProgress;

    /// <summary>Direction the route goes at the current progress position</summary>
    public Vector3 RouteDirection => routeDirection;

    /// <summary>Nearest point on the route to the drone</summary>
    public Vector3 NearestRoutePoint => nearestRoutePoint;

    /// <summary>Is a waypoint/route assigned and valid?</summary>
    public bool HasWaypoint => waypoint != null;

    /// <summary>Overall trajectory deviation (sum of sampled deviations, circuit only)</summary>
    public float OverallTrajectoryDeviation => overallDistanceFromTrajectory;

    // ──────────── Private State ────────────
    float progressDistance;
    float lastProgressDistance;
    float progressDeltaPerFrame;
    float deviationFromRoute;
    float normalizedProgress;
    float overallDistanceFromTrajectory;
    Vector3 routeDirection = Vector3.forward;
    Vector3 nearestRoutePoint;
    bool needToRecalculateDistanceFromCircuit = false;

    float timer = 0.1f;
    float actualTimer = 1;

    // cached reference (only used in Classic mode)
    droneMovementController dmc;

    // ──────────── Public API ────────────

    /// <summary>
    /// Assign a waypoint route. Works for both WaypointCircuit and singlePoint.
    /// </summary>
    public void SetWaypoint(Waypoint wpc)
    {
        waypoint = wpc;
        progressDistance = 0;
        lastProgressDistance = 0;
        progressDeltaPerFrame = 0;
        deviationFromRoute = 0;
        normalizedProgress = 0;
        overallDistanceFromTrajectory = 0;

        if (wpc == null) return;

        if (!wpc.isCircuit())
            ((singlePoint)wpc).setHome(transform);
        else
            hasToRecalculateDistance();
    }

    /// <summary>Legacy setter name kept for compatibility</summary>
    public void setWaypoint(Waypoint wpc) => SetWaypoint(wpc);

    /// <summary>Reset all tracking state (call from OnEpisodeBegin)</summary>
    public void ResetProgress()
    {
        progressDistance = 0;
        lastProgressDistance = 0;
        progressDeltaPerFrame = 0;
        deviationFromRoute = 0;
        normalizedProgress = 0;
        overallDistanceFromTrajectory = 0;
        needToRecalculateDistanceFromCircuit = true;
    }

    /// <summary>Force recalculation of position on circuit</summary>
    public void hasToRecalculateDistance() { needToRecalculateDistanceFromCircuit = true; }

    /// <summary>Gets the route position at current progress</summary>
    public Vector3 getRoutePosition()
    {
        if (waypoint == null) return transform.position;
        return waypoint.GetRoutePosition(progressDistance);
    }

    // ──────────── Core Update ────────────

    void Start()
    {
        dmc = GetComponentInParent<droneMovementController>();
        if (dmc == null)
            dmc = GetComponent<droneMovementController>();
    }

    void Update()
    {
        if (waypoint == null) return;

        // ── Update metrics + optionally control drone ──
        if (waypoint.isCircuit())
            UpdateCircuit();
        else
            UpdateSinglePoint();

        // ── Compute per-frame progress delta ──
        progressDeltaPerFrame = progressDistance - lastProgressDistance;
        lastProgressDistance = progressDistance;
    }

    // ──────────── Circuit Tracking ────────────

    void UpdateCircuit()
    {
        // Get circuit length for normalization
        WaypointCircuit wc = waypoint as WaypointCircuit;
        float circuitLength = wc != null ? wc.getTotalLengthOfCircuit() : 1f;
        if (circuitLength <= 0f) circuitLength = 1f;

        // Periodic deviation sampling
        if (actualTimer >= 0)
            actualTimer -= Time.deltaTime;
        else
        {
            actualTimer += timer;
            SampleTrajectoryDeviation(circuitLength);
        }

        // Route position & direction
        nearestRoutePoint = getRoutePosition();
        deviationFromRoute = Vector3.Distance(transform.position, nearestRoutePoint);

        Waypoint.RoutePoint rp = waypoint.GetRoutePoint(progressDistance);
        routeDirection = rp.direction;

        // Progress tracking
        needToRecalculateDistanceFromCircuit =
            Vector3.Distance(transform.position, nearestRoutePoint) > 10f
            || needToRecalculateDistanceFromCircuit;

        if (needToRecalculateDistanceFromCircuit)
        {
            progressDistance = waypoint.getNearestPointTo(transform.position);
            needToRecalculateDistanceFromCircuit = false;
        }
        else
        {
            Waypoint.RoutePoint progressPoint = waypoint.GetRoutePoint(progressDistance);
            Vector3 progressDelta = progressPoint.position - transform.position;
            if (Vector3.Dot(progressDelta, progressPoint.direction) < 0)
                progressDistance += progressDelta.magnitude * 0.5f;
        }

        normalizedProgress = progressDistance / circuitLength;

        // ── Update target + droneMovementController (Classic mode only) ──
        if (!readOnlyMode && dmc != null && target != null)
        {
            target.position = waypoint.GetRoutePoint(progressDistance + lookAheadForTargetOffset).position;
            target.rotation = Quaternion.LookRotation(waypoint.GetRoutePoint(progressDistance).direction);

            dmc.setRoutePos(nearestRoutePoint);
            float distToRoute = Vector3.Distance(transform.position, nearestRoutePoint);
            dmc.setLookingPoint(waypoint.GetRoutePosition(progressDistance + distanceOfPointToLookAt - distToRoute));
            dmc.stayOnFixedPoint = false;
        }
    }

    void SampleTrajectoryDeviation(float circuitLength)
    {
        if (target == null) return;

        int nOfPoints = (int)lookAheadForTargetOffset;
        if (nOfPoints < 2) nOfPoints = 2;

        Vector3[] pBetween = waypoint.pointsBetween(getRoutePosition(), target.transform.position, nOfPoints);
        if (pBetween == null || pBetween.Length == 0) return;

        Vector3[] destPoints = new Vector3[pBetween.Length];
        for (int i = 0; i < pBetween.Length; i++)
            destPoints[i] = GetPerpendicularPoint(transform.position, target.position, pBetween[i]);

        overallDistanceFromTrajectory = 0;
        for (int i = 0; i < pBetween.Length - 1; i++)
            overallDistanceFromTrajectory += Vector3.Distance(pBetween[i], destPoints[i]);

        if (overallDistanceFromTrajectory > maxDistFromCircuit)
            lookAheadForTargetOffset -= 0.25f;
        else
            lookAheadForTargetOffset += 0.25f;

        lookAheadForTargetOffset = droneSettings.keepOnRange(lookAheadForTargetOffset, 3f, 12f);
    }

    // ──────────── SinglePoint Tracking ────────────

    void UpdateSinglePoint()
    {
        nearestRoutePoint = getRoutePosition();
        deviationFromRoute = Vector3.Distance(transform.position, nearestRoutePoint);
        routeDirection = (nearestRoutePoint - transform.position).normalized;
        normalizedProgress = 0f; // not applicable for single point

        if (!readOnlyMode && dmc != null)
        {
            dmc.setRoutePos(nearestRoutePoint);
            dmc.stayOnFixedPoint = true;
            if (target != null)
                target.position = nearestRoutePoint;

            singlePoint sp = waypoint as singlePoint;
            if (sp != null)
                dmc.setLookingPoint(sp.getLookingAtPoint());
        }
    }

    // ──────────── Geometry Helper ────────────

    Vector3 GetPerpendicularPoint(Vector3 ptR1, Vector3 ptR2, Vector3 point)
    {
        Vector2 A = new Vector2(ptR1.x, ptR1.z);
        Vector2 B = new Vector2(ptR2.x, ptR2.z);
        Vector2 C = new Vector2(point.x, point.z);

        float m = (B.y - A.y) / (B.x - A.x);
        float k = m * C.y + C.x;
        float n = B.y - A.y;
        float o = B.x - A.x;
        float l = o * A.y - n * A.x;

        float newX = -(l * m - k * o) / (o + m * n);
        float newY = (k * n + l) / (o + m * n);

        return new Vector3(newX, point.y, newY);
    }

    // ──────────── Gizmos ────────────

    public bool drawGizmos = true;
    void OnDrawGizmos()
    {
        if (!Application.isPlaying || !drawGizmos || waypoint == null) return;

        // Route-Position Marker
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(nearestRoutePoint, 0.3f);

        // Deviation Line (drone → route)
        Gizmos.color = deviationFromRoute > 3f ? Color.red : Color.green;
        Gizmos.DrawLine(transform.position, nearestRoutePoint);

        // Target + direction (if available)
        if (target != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawLine(transform.position, target.position);
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(target.position, target.position + target.forward);
        }
    }
}
