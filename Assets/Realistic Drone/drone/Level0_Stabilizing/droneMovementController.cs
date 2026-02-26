using UnityEngine;
using System.Collections;
using System;


public class droneMovementController : MonoBehaviour
{

    #region phisical Parts and related functions

    // Sensors of the drone (have to be associated to the sensors object in the drone model)
    public Gyro gyro;
    public Accelerometer acc;
    public Barometer bar;
    public GPS gps;
    public Magnetometer mag;

    // Rotors of the drone (have to be associated to the four rotors of the drone, with the order V1,O1,V2,O2)
    public rotor helixV1;
    public rotor helixV2;
    public rotor helixO1;
    public rotor helixO2;

    // PIDs of the drone. Instanciated in run-time
    public PID yawPID;
    public PID rollPID;
    public PID pitchPID;
    public PID yPID;
    public PID zPID;
    public PID xPID;

    // Rigidbody – lives on the parent Agent GO, found via GetComponentInParent
    Rigidbody rb;

    /// <summary>
    /// Used to simulate the torque applied to the drone, following the differences between the rotors power.
    /// Uses physics-based AddTorque on the Rigidbody for realistic behaviour.
    /// </summary>
    /// <param name="amount">sum of the results of <c>verse(Rotor r)</c>, applied over every rotor</param>
    void applyTorque(float amount)
    {
        if (rb != null)
            rb.AddTorque(rb.transform.up * amount * Time.deltaTime, ForceMode.Force);
    }

    /// <summary>
    /// Calculates the amount of torque that a single rotor is generating over the entire system.
    /// <para>The sum of the results of this funcion, applied to the four rotors have to be passed as 
    /// parameter to the function <c>applyTorque(float amount)</c></para>
    /// </summary>
    /// <param name="r">Rotor class</param>
    /// <returns>The amount of torque that a single rotor is generating over the entire system</returns>
    float torqueGeneratedBy(rotor r) { return (r.counterclockwise ? -1 : 1) * denormalizeTorque(r.getPower()) * 10; }

    /// <summary>
    /// Transform the power calculated by the algorithms (that is always between 0 and 1) so it can be used by the rotors class
    /// </summary>
    /// <param name="pow">Power of the rotor, calculated by the algorithms</param>
    /// <returns>A value between [saturationValues.minRotationSpeed, saturationValues.maxRotationSpeed] </returns>
    float denormalizePower(float pow) { return denormalize(pow, droneSettings.saturationValues.minRotationSpeed, droneSettings.saturationValues.maxRotationSpeed); }

    /// <summary>
    /// Transform the power calculated by the algorithms (that is always between 0 and 1) so it can be used to calculate the overall torque
    /// </summary>
    /// <param name="pow">Power of the rotor, calculated by the algorithms</param>
    /// <returns>A value between [saturationValues.minTorque, saturationValues.maxTorque] </returns>
    float denormalizeTorque(float pow) { return denormalize(pow, droneSettings.saturationValues.minTorque, droneSettings.saturationValues.maxTorque); }

    /// <summary>
    /// Generic function used to denormalize
    /// </summary>
    /// <param name="pow">Number to denormalize that belongs to the interval [0,1]</param>
    /// <param name="lBound">Lower bound of the denormalized number</param>
    /// <param name="uBound">Upper bound of the denormalized number</param>
    /// <returns>The number passed as argument, denormalized in the interval [lBound,uBound]</returns>
    float denormalize(float pow, float lBound, float uBound) { return pow * (uBound - lBound) + lBound; }

    /// <summary>
    /// Keep a number in the interval [0,1], truncating it if it is outside that range
    /// </summary>
    /// <param name="num">Number that has to be maintained in the interval [0,1]</param>
    /// <returns>if (num € [0,1]) -> num; else if (num is less than 0) -> 0; else -> 1</returns>
    float keepOnRange01(float num) { return (float.IsNaN(num) ? 0 : droneSettings.keepOnRange(num, 0f, 1f)); }
    #endregion

    #region targets 

    // Variables that represent the ideal-State of the drone. They are used to calculate the error
    public float idealPitch = 0;
    public float idealRoll = 0;
    public float idealYaw = 0;
    public float targetX = 0;
    public float targetY = 10;
    public float targetZ = 0;

    // Point used to calculate the local Z position of the drone
    public Transform target;
    // Point used to calculate the local X position of the drone
    private Vector3 routePosition;
    /// <summary>
    /// Sets the routePosition, used by the spatial-stabilization algorithm to move
    /// </summary>
    /// <param name="v">Position in the route</param>
    public void setRoutePos(Vector3 v) { routePosition = v; }

    // Point that the drone has to look at. Determine the orientation
    private Vector3 lookingAtPoint;
    /// <summary>
    /// Sets the lookingPoint, used by the Yaw-stabilization algorithm
    /// </summary>
    /// <param name="v">Point the drone has to look at</param>
    public void setLookingPoint(Vector3 v) { lookingAtPoint = v; }

    // Indicates if the drone has to stabilize itself to the routePosition or can keep following the target
    public bool stayOnFixedPoint = false;
    public void followTarget(bool b) { stayOnFixedPoint = b; }

    #endregion

    #region internal inputs
    // This part permits to the optimizations algorithms to modify directly the settings of the drone    

    // if this value is TRUE, the drone is using these constants instead of the ones saved in the settings
    bool testing = false;
    float constVertVel, constVertAcc;
    float constAxisVel, constAxisAcc;
    float constYawVel;
    float constHorizVel, constHorizAcc;
    /// <summary>
    /// Sets the constants used in the stabilization algorithms
    /// <para>This function is used ONLY by the optimizations algorithm (Genetic and twiddle)</para>
    /// </summary>
    public void setConsts(float vVel, float vAcc, float aVel, float aAcc, float yVel, float orVel, float orAcc)
    {
        testing = true;
        constVertVel = vVel;
        constVertAcc = vAcc;
        constAxisVel = aVel;
        constAxisAcc = aAcc;
        constYawVel = yVel;
        constHorizVel = orVel;
        constHorizAcc = orAcc;
    }
    /// <summary>
    /// Sets the PIDs of the drone
    /// <para>This function is used ONLY by the optimizations algorithm (Genetic and twiddle)</para>
    /// </summary>
    public void setKs(PID yPID, PID zPID, PID xPID, PID pitchPID, PID rollPID, PID yawPID)
    {
        //testing = true;
        this.xPID = xPID;
        this.zPID = zPID;
        this.yPID = yPID;
        this.pitchPID = pitchPID;
        this.rollPID = rollPID;
        this.yawPID = yawPID;
    }
    #endregion

    #region ML-Agent Integration

    [Header("ML-Agent Integration")]
    [Tooltip("When true, receives commands from DroneMLAgent instead of waypoint system")]
    public bool controlledByMLAgent = false;

    [Tooltip("Max tilt angle (normalized) for pitch/roll from agent commands")]
    public float maxTiltFromAgent = 0.40f;

    [Tooltip("Vertical speed multiplier for gaz command (m/s per unit)")]
    public float gazSpeedMultiplier = 5f;

    [Tooltip("Yaw rate sensitivity for turn command")]
    public float yawRateMultiplier = 0.3f;

    [Tooltip("Min/Max target altitude for agent mode")]
    public float minAltitude = 0.5f;
    public float maxAltitude = 50f;

    // Agent command inputs – set each FixedUpdate by DroneMLAgent
    float agentCmdPitch;
    float agentCmdRoll;
    float agentCmdGaz;
    float agentCmdYaw;

    /// <summary>
    /// Set flight commands from ML-Agent. Mimics Parrot AR.Drone 2.0 AT*PCMD.
    /// <para>pitch  in [-1,1]: forward/backward tilt (positive = forward)</para>
    /// <para>roll   in [-1,1]: left/right tilt (positive = right)</para>
    /// <para>gaz    in [-1,1]: vertical speed (positive = up)</para>
    /// <para>yaw    in [-1,1]: rotation speed (positive = clockwise from above)</para>
    /// </summary>
    public void SetAgentCommand(float pitch, float roll, float gaz, float yaw)
    {
        agentCmdPitch = pitch;
        agentCmdRoll = roll;
        agentCmdGaz = gaz;
        agentCmdYaw = yaw;
    }

    /// <summary>
    /// Reset flight controller state for a new ML-Agent episode.
    /// Called by DroneMLAgent.OnEpisodeBegin.
    /// </summary>
    public void ResetForEpisode(float startHeight)
    {
        // Re-initialise PIDs (clears accumulated error)
        yPID = new PID(droneSettings.verticalPID_P, droneSettings.verticalPID_I, droneSettings.verticalPID_D, droneSettings.verticalPID_U);
        yawPID = new PID(droneSettings.yawPID_P, droneSettings.yawPID_I, droneSettings.yawPID_D, droneSettings.yawPID_U);
        rollPID = new PID(droneSettings.orizPID_P, droneSettings.orizPID_I, droneSettings.orizPID_D, droneSettings.orizPID_U);
        pitchPID = new PID(droneSettings.orizPID_P, droneSettings.orizPID_I, droneSettings.orizPID_D, droneSettings.orizPID_U);
        zPID = new PID(droneSettings.axisPID_P, droneSettings.axisPID_I, droneSettings.axisPID_D, droneSettings.axisPID_U);
        xPID = new PID(droneSettings.axisPID_P, droneSettings.axisPID_I, droneSettings.axisPID_D, droneSettings.axisPID_U);

        // Reset rotor powers to neutral hover baseline
        pV1 = pV2 = pO1 = pO2 = 0.5f;

        // Reset targets and commands
        targetY = startHeight;
        idealPitch = idealRoll = idealYaw = 0f;
        agentCmdPitch = agentCmdRoll = agentCmdGaz = agentCmdYaw = 0f;

        startAfter = 0.05f;
    }

    #endregion

    #region outputs to the rotors

    // variables where is stored, in a range [0,1], the power of each rotor
    public float pV1;
    public float pV2;
    public float pO1;
    public float pO2;

    /// <summary>
    /// Modify the power of all 4 rotors, in order to modify the height of the drone
    /// </summary>
    /// <param name="intensity">Magnitute of the modification</param>
    private void modifyAllRotorsRotation(float intensity)
    {
        pV1 += intensity;
        pV2 += intensity;
        pO1 += intensity;
        pO2 += intensity;
    }

    /// <summary>
    /// Modify the power of the rotors, in order to modify the roll of the drone
    /// </summary>
    /// <param name="intensity">Magnitute of the modification</param>
    private void modifyRollRotorsRotation(float intensity)
    {
        pV1 += intensity; pV2 -= intensity;
        pO2 += intensity; pO1 -= intensity;
    }

    /// <summary>
    /// Modify the power of the rotors, in order to modify the pitch of the drone
    /// </summary>
    /// <param name="intensity">Magnitute of the modification</param>
    private void modifyPitchRotorsRotation(float intensity)
    {
        pV1 += intensity; pV2 -= intensity;
        pO1 += intensity; pO2 -= intensity;
    }

    /// <summary>
    /// Modify the power of the rotors, in order to modify the yaw of the drone
    /// </summary>
    /// <param name="intensity">Magnitute of the modification</param>
    private void modifyPairsRotorsRotation(float intensity)
    {
        pV1 += intensity;
        pV2 += intensity;
        pO1 -= intensity;
        pO2 -= intensity;
    }

    #endregion

    #region Stabilizations

    /// <summary>
    /// Vertical Stabilization algorithm
    /// </summary>
    /// <param name="targetAltitude">Altitude that we want to reach. It'll be compared with the actual to extract the error</param>
    void yStabilization(float targetAltitude)
    {
        //calculates the error and extracts the measurements from the sensors
        float distanceToPoint = (targetAltitude - bar.getHeight());

        // adding the value to the test class
        //tHeight.addValue(distanceToPoint);
        float acc = bar.getverticalAcc();
        float vel = bar.getverticalSpeed();

        //calculates the idealVelocity, we'll use this to extract an error that will be given to the PID
        float idealVel = distanceToPoint * (testing ? constVertVel : droneSettings.constVerticalIdealVelocity);
        idealVel = droneSettings.keepOnRange(idealVel, droneSettings.saturationValues.minVerticalVel, droneSettings.saturationValues.maxVerticalVel);

        //calculates the idealAcc, we'll use this to extract an error that will be given to the PID
        float idealAcc = (idealVel - vel) * (testing ? constVertAcc : droneSettings.constVerticalIdealAcceler);
        idealAcc = droneSettings.keepOnRange(idealAcc, droneSettings.saturationValues.minVerticalAcc, droneSettings.saturationValues.maxVerticalAcc);

        //Error used by the PID
        float Err = (idealAcc - acc);

        //If this is TRUE we are near the point and with a low velocity. It is not necessary to modify the Power
        if (Mathf.Abs(vel) + Mathf.Abs(distanceToPoint) > 0.005f)
            //modifying the rotors rotation, using the output of the PID
            modifyAllRotorsRotation(yPID.getU(Err, Time.deltaTime));
    }

    /// <summary>
    /// Roll Stabilization algorithm
    /// </summary>
    /// <param name="idealRoll">Roll value that we want to reach. It'll be compared with the actual to extract the error</param>
    void rollStabilization(float idealRoll)
    {
        //calculates the error and extracts the measurements from the sensors
        float rollDistance = idealRoll - this.gyro.getRoll();
        float acc = this.gyro.getRollAcc();
        float vel = this.gyro.getRollVel();

        //calculates idealVelocity and idealAcceleration, we'll use this to extract an error that will be given to the PID
        float idealVel = rollDistance * (testing ? constHorizVel : droneSettings.constHorizontalIdealVelocity);
        float idealAcc = (idealVel - vel) * (testing ? constHorizAcc : droneSettings.constHorizontalIdealAcceler);

        //Error used by the PID
        float Err = (idealAcc - acc);

        //modifying the rotors rotation, using the output of the PID
        modifyRollRotorsRotation(rollPID.getU(-Err, Time.deltaTime));
    }

    /// <summary>
    /// Pitch Stabilization algorithm
    /// </summary>
    /// <param name="idealPitch">Pitch value that we want to reach. It'll be compared with the actual to extract the error</param>
    void pitchStabilization(float idealPitch)
    {
        //calculates the error and extracts the measurements from the sensors
        float pitchDistance = idealPitch - this.gyro.getPitch();
        float acc = this.gyro.getPitchAcc();
        float vel = this.gyro.getPitchVel();

        //calculates idealVelocity and idealAcceleration, we'll use this to extract an error that will be given to the PID
        float idealVel = pitchDistance * (testing ? constHorizVel : droneSettings.constHorizontalIdealVelocity);
        float idealAcc = (idealVel - vel) * (testing ? constHorizAcc : droneSettings.constHorizontalIdealAcceler);

        //Error used by the PID
        float Err = (idealAcc - acc);

        //modifying the rotors rotation, using the output of the PID
        modifyPitchRotorsRotation(pitchPID.getU(-Err, Time.deltaTime));
    }

    /// <summary>
    /// Yaw Stabilization algorithm
    /// </summary>
    /// <param name="idealYaw">Yaw value that we want to reach. It'll be compared with the actual to extract the error</param>
    /// <returns>The absolute value of the error, used to decrease the effect of the others stabilization algorithms</returns>
    float yawStabilization(float idealYaw)
    {
        //calculates the error and extracts the measurements from the sensors
        float yawDistance = mag.getYaw() - idealYaw;
        yawDistance = (Mathf.Abs(yawDistance) < 1 ? yawDistance : (yawDistance > 0 ? yawDistance - 2 : yawDistance + 2));

        //calculates idealVelocity, we'll use this to extract an error that will be given to the PID
        float vel = mag.getYawVel();
        float idealVel = -yawDistance * (testing ? constYawVel : droneSettings.constYawIdealVelocity);

        //Error used by the PID
        float Err = (idealVel - vel);
        Err *= Mathf.Abs(yawDistance) * (Mathf.Abs(yawDistance) > 0.3f ? -10 : -50);

        //modifying the rotors rotation, using the output of the PID
        float res = yawPID.getU(Err, Time.deltaTime);
        modifyPairsRotorsRotation(res);

        return Math.Abs(idealYaw - mag.getYaw());
    }

    /// <summary>
    /// Z Stabilization algorithm
    /// </summary>
    /// <param name="targetZ">Z value that we want to reach. It'll be compared with the actual to extract the error</param>
    /// <returns>Returns an error that has to be given to the PITCH_stabilization function</returns>
    float zStabilization(float targetZ)
    {
        //calculates the error and extracts the measurements from the sensors 
        float distanceToPoint = droneSettings.keepOnAbsRange(targetZ, 30f);
        float acc = this.acc.getLinearAcceleration().z;
        float vel = this.acc.getLocalLinearVelocity().z;
        float yawVel = this.mag.getYawVel();

        //calculates idealVelocity and idealAcceleration, we'll use this to extract an error that will be given to the PID
        float idealVel = distanceToPoint * (testing ? constAxisVel : droneSettings.constAxisIdealVelocity);
        idealVel = droneSettings.keepOnAbsRange(idealVel, droneSettings.saturationValues.maxHorizontalVel);
        float idealAcc = (idealVel - vel) * (testing ? constAxisAcc : droneSettings.constAxisIdealAcceler);
        idealAcc = droneSettings.keepOnAbsRange(idealAcc, 3f);

        //Error used by the PID
        float Err = (idealAcc - acc);
        Err *= 1 - keepOnRange01(Math.Abs(idealYaw - mag.getYaw()));

        //dS.addLine(new float[] { Err, distanceToPoint, vel, idealVel, acc, idealAcc  });      // use this to save the data to the DataSaver class
        return zPID.getU(Err, Time.deltaTime);
    }

    /// <summary>
    /// X Stabilization algorithm
    /// </summary>
    /// <param name="targetX">X value that we want to reach. It'll be compared with the actual to extract the error</param>
    /// <returns>Returns an error that has to be given to the ROLL_stabilization function</returns>
    float xStabilization(float targetX)
    {
        //calculates the error and extracts the measurements from the sensors
        float distanceToPoint = droneSettings.keepOnAbsRange(targetX, 30f);
        float acc = this.acc.getLinearAcceleration().x;
        float vel = this.acc.getLocalLinearVelocity().x;

        //calculates idealVelocity and idealAcceleration, we'll use this to extract an error that will be given to the PID
        float idealVel = distanceToPoint * (testing ? constAxisVel : droneSettings.constAxisIdealVelocity);
        idealVel = droneSettings.keepOnAbsRange(idealVel, droneSettings.saturationValues.maxHorizontalVel);
        float idealAcc = (idealVel - vel) * (testing ? constAxisAcc : droneSettings.constAxisIdealAcceler);
        idealAcc = droneSettings.keepOnAbsRange(idealAcc, 3f);

        //Error used by the PID
        float Err = (idealAcc - acc);
        Err *= 1 - keepOnRange01(Math.Abs(idealYaw - mag.getYaw()));

        return xPID.getU(Err, Time.deltaTime);
    }

    #endregion

    // classes used to print lines (direction vectors for example). Used for debugging
    lineDrawer linedrawer;
    int ticket1;
    int ticket2;
    int ticket3;
    int ticket4;

    // classes used to save the stats of the drone. Used for debugging
    dataSaver dS;
    dataSaver dSOut;

    //Test tHeight;

    /// <summary>
    /// Function called before of the first update
    /// </summary>
    void Start()
    {
        // Find the Rigidbody (on parent Agent GO or on self)
        rb = GetComponentInParent<Rigidbody>();

        // Check for optional optimisation scripts (Realistic Drone package)
        var genetic = gameObject.GetComponent<geneticBehaviour>();
        var twiddle = gameObject.GetComponent<twiddleBehaviour>();
        var config = gameObject.GetComponent<configReader>();

        bool externalPIDInit = (genetic != null && genetic.enabled)
                            || (twiddle != null && twiddle.enabled)
                            || (config != null && config.enabled);

        if (!externalPIDInit)
        {
            yPID = new PID(droneSettings.verticalPID_P, droneSettings.verticalPID_I, droneSettings.verticalPID_D, droneSettings.verticalPID_U);
            yawPID = new PID(droneSettings.yawPID_P, droneSettings.yawPID_I, droneSettings.yawPID_D, droneSettings.yawPID_U);
            rollPID = new PID(droneSettings.orizPID_P, droneSettings.orizPID_I, droneSettings.orizPID_D, droneSettings.orizPID_U);
            pitchPID = new PID(droneSettings.orizPID_P, droneSettings.orizPID_I, droneSettings.orizPID_D, droneSettings.orizPID_U);
            zPID = new PID(droneSettings.axisPID_P, droneSettings.axisPID_I, droneSettings.axisPID_D, droneSettings.axisPID_U);
            xPID = new PID(droneSettings.axisPID_P, droneSettings.axisPID_I, droneSettings.axisPID_D, droneSettings.axisPID_U);
        }

        // LineDrawer is optional (only used in waypoint debug mode)
        linedrawer = gameObject.GetComponent<lineDrawer>();
        if (linedrawer != null)
        {
            ticket1 = linedrawer.getTicket();
            ticket2 = linedrawer.getTicket();
            ticket3 = linedrawer.getTicket();
            ticket4 = linedrawer.getTicket();
        }
    }


    public bool save = false;
    /// <summary>
    /// Function called each frame
    /// </summary>
    void Update()
    {
        if (save) { save = false; if (dS != null) dS.saveOnFile(); }
    }

    float startAfter = 0.1f;
    /// <summary>
    /// Function at regular time interval
    /// </summary>
    void FixedUpdate()
    {
        // wait briefly to avoid initialisation problems
        if ((startAfter -= Time.deltaTime) > 0) return;

        // ══════════ Target calculation ══════════
        if (controlledByMLAgent)
        {
            // ── ML-Agent mode: AT*PCMD-style commands ──
            // Pitch & Roll: agent commands directly set ideal tilt angles
            idealPitch = agentCmdPitch * maxTiltFromAgent;
            idealRoll = agentCmdRoll * maxTiltFromAgent;

            // Gaz: integrate vertical speed command into altitude target
            targetY += agentCmdGaz * gazSpeedMultiplier * Time.fixedDeltaTime;
            targetY = Mathf.Clamp(targetY, minAltitude, maxAltitude);

            // Yaw: set target yaw relative to current heading
            idealYaw = mag.getYaw() + agentCmdYaw * yawRateMultiplier;
            idealYaw = Mathf.Clamp(idealYaw, -1f, 1f);
        }
        else
        {
            // ── Original waypoint mode ──
            if (stayOnFixedPoint)
            {
                Vector3 p = mag.worldToLocalPoint(routePosition, target.position);
                targetZ = p.z;
                targetX = p.x;
                targetY = routePosition.y;
            }
            else
            {
                targetZ = mag.worldToLocalPoint(target.position, lookingAtPoint).z;
                targetX = mag.worldToLocalPoint(routePosition, lookingAtPoint).x;
                targetY = (routePosition.y + target.position.y) / 2f;
            }

            Vector3 thrustVector = Quaternion.AngleAxis(-45, Vector3.up) * new Vector3(targetX, targetY - transform.position.y, targetZ);
            Vector3 xComponent = Quaternion.AngleAxis(-45, Vector3.up) * new Vector3(targetX, 0, 0);
            Vector3 zComponent = Quaternion.AngleAxis(-45, Vector3.up) * new Vector3(0, 0, targetZ);

            if (linedrawer != null)
            {
                linedrawer.drawPosition(ticket3, thrustVector);
                linedrawer.drawPosition(ticket1, xComponent);
                linedrawer.drawPosition(ticket2, zComponent);
            }

            idealPitch = droneSettings.keepOnAbsRange(zStabilization(targetZ), 0.40f);
            idealRoll = droneSettings.keepOnAbsRange(xStabilization(targetX), 0.40f);
            idealYaw = mag.getYawToCenterOn(lookingAtPoint);
        }

        // ══════════ Attitude stabilization (common) ══════════
        yStabilization(targetY);
        pitchStabilization(idealPitch);
        rollStabilization(idealRoll);
        float yawErr = yawStabilization(idealYaw);

        if (!controlledByMLAgent)
            followTarget(yawErr < 0.22f);

        // ══════════ Rotor output ══════════
        pV1 = keepOnRange01(pV1);
        pV2 = keepOnRange01(pV2);
        pO1 = keepOnRange01(pO1);
        pO2 = keepOnRange01(pO2);
        helixV1.setPower(denormalizePower(pV1));
        helixV2.setPower(denormalizePower(pV2));
        helixO1.setPower(denormalizePower(pO1));
        helixO2.setPower(denormalizePower(pO2));

        // Torque from differential rotor speeds
        applyTorque(torqueGeneratedBy(helixV1) + torqueGeneratedBy(helixV2)
                  + torqueGeneratedBy(helixO1) + torqueGeneratedBy(helixO2));
    }


}