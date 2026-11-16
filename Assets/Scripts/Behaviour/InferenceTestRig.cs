using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Test bench for the digital twin: runs the trained policy plus the mission state machine in the simulator and
/// checks scripted scenarios (target ahead / behind / vanishing / static, battery drop, link loss).
/// Operator buttons and hotkeys mirror the phone UI. Command line: <c>-scenario all|1..6</c> runs unattended
/// and exits the Editor with code 0 (all passed) or 1.
/// </summary>
public sealed class InferenceTestRig : MonoBehaviour
{
    public DroneBehaviour behaviour;
    public SimulatedDroneController drone;
    public DroneFollowSentisPerception perception;
    public Transform target;
    public DroneFollowPathWalker walker;
    public DroneFollowPath[] paths;
    [Range(1f, 4f)] public float timeScale = 1f;

    static readonly string[] ScenarioNames =
    {
        "1 Follow target ahead", "2 Search target behind", "3 Target vanishes", "4 Battery drop", "4b Battery sag", "5 Link loss", "6 Static target"
    };

    Vector3 _spawnPosition;
    Quaternion _spawnRotation;
    Vector3 _targetPosition;
    Quaternion _targetRotation;
    readonly HashSet<BehaviourState> _visited = new HashSet<BehaviourState>();
    readonly List<string> _results = new List<string>();
    string _running = "";
    GUIStyle _label;
    GUIStyle _button;

    void Start()
    {
        _spawnPosition = drone.transform.position;
        _spawnRotation = drone.transform.rotation;
        _targetPosition = target.position;
        _targetRotation = target.rotation;
        Time.timeScale = timeScale;
        foreach (var skinned in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include))
            Debug.Log($"[Scenario] character '{skinned.transform.root.name}/{skinned.name}' active={skinned.gameObject.activeInHierarchy} pos={skinned.transform.position}");
        if (TryGetArgument("-scenario", out string which))
            StartCoroutine(RunFromCommandLine(which));
    }

    static bool TryGetArgument(string name, out string value)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
            {
                value = args[i + 1];
                return true;
            }
        value = null;
        return false;
    }

    void Update()
    {
        _visited.Add(behaviour.State);
        // Like on the phone the target is locked once; afterwards only appearance matching may reacquire it,
        // otherwise auto-lock would hand the lock to any other person once the real target is gone.
        if (behaviour.State == BehaviourState.Follow && perception.autoLockTarget && perception.Tracker.HasLock)
            perception.autoLockTarget = false;
        Time.timeScale = timeScale;
    }

    IEnumerator RunFromCommandLine(string which)
    {
        yield return new WaitForSeconds(2f);
        int failures = 0;
        for (int i = 0; i < ScenarioNames.Length; i++)
        {
            if (which != "all" && !ScenarioNames[i].StartsWith(which + " "))
                continue;
            yield return RunScenario(i);
            if (!_results[_results.Count - 1].Contains("PASS"))
                failures++;
        }
        Debug.Log($"[Scenario] DONE {_results.Count} scenario(s), {failures} failed");
#if UNITY_EDITOR
        if (Application.isBatchMode)
            UnityEditor.EditorApplication.Exit(failures == 0 ? 0 : 1);
#else
        Application.Quit(failures == 0 ? 0 : 1);
#endif
    }

    // ───────────────────────── scenarios ─────────────────────────

    IEnumerator RunScenario(int index)
    {
        _running = ScenarioNames[index];
        _visited.Clear();
        bool ok = false;
        string detail = "";
        float startTime = Time.time;

        switch (index)
        {
            case 0: // target ahead, walking
                yield return Prepare(false);
                yield return WaitForState(BehaviourState.Hover, 20f);
                Operator(OperatorCommand.Follow);
                yield return WaitForState(BehaviourState.Follow, 15f);
                StartWalking();
                yield return HoldMeasured(25f);
                ok = _followRatio >= 0.6f && _maxDistance < 15f;
                detail = $"in Follow {_followRatio:P0} of 25 s, max distance {_maxDistance:F1} m";
                break;

            case 1: // target behind: search must find it
                yield return Prepare(true);
                yield return WaitForState(BehaviourState.Hover, 20f);
                Operator(OperatorCommand.Search);
                yield return WaitForState(BehaviourState.Follow, 60f);
                ok = behaviour.State == BehaviourState.Follow && _visited.Contains(BehaviourState.Search);
                detail = $"found after {Time.time - startTime:F0} s, state={behaviour.State}";
                break;

            case 2: // target vanishes: LostTarget -> Search -> Landing -> Idle
                yield return Prepare(false);
                yield return WaitForState(BehaviourState.Hover, 20f);
                Operator(OperatorCommand.Follow);
                yield return WaitForState(BehaviourState.Follow, 15f);
                yield return Hold(3f);
                target.gameObject.SetActive(false);
                behaviour.forceTargetLost = true; // YOLO/re-id on the twin's scenery re-lock false persons, so force the lost path
                yield return Hold(4f);
                Debug.Log($"[Scenario] 4 s after the target vanished: state={behaviour.State} visible={perception.Current.Visible} people={perception.PeopleInView} lock={perception.Tracker.HasLock}");
                yield return WaitForState(BehaviourState.Idle, 150f);
                ok = _visited.Contains(BehaviourState.LostTarget) && _visited.Contains(BehaviourState.Search) &&
                     _visited.Contains(BehaviourState.Landing) && behaviour.State == BehaviourState.Idle;
                detail = "visited " + string.Join(",", _visited);
                behaviour.forceTargetLost = false;
                target.gameObject.SetActive(true);
                break;

            case 3: // battery really empty
                yield return Prepare(false);
                yield return WaitForState(BehaviourState.Hover, 20f);
                behaviour.batteryOverride = 5f;
                yield return WaitForState(BehaviourState.Idle, 90f);
                ok = _visited.Contains(BehaviourState.Failsafe) && behaviour.State == BehaviourState.Idle;
                detail = "visited " + string.Join(",", _visited);
                behaviour.batteryOverride = -1f;
                break;

            case 4: // short battery sag must not land
                yield return Prepare(false);
                yield return WaitForState(BehaviourState.Hover, 20f);
                yield return Hold(3f);
                behaviour.batteryOverride = 5f;
                yield return Hold(1f);
                behaviour.batteryOverride = -1f;
                yield return Hold(8f);
                ok = behaviour.State == BehaviourState.Hover;
                detail = $"state={behaviour.State}";
                break;

            case 5: // link loss
                yield return Prepare(false);
                yield return WaitForState(BehaviourState.Hover, 20f);
                behaviour.simulateLinkLoss = true;
                yield return WaitForState(BehaviourState.Failsafe, 5f);
                ok = behaviour.State == BehaviourState.Failsafe || _visited.Contains(BehaviourState.Failsafe);
                detail = "visited " + string.Join(",", _visited);
                behaviour.simulateLinkLoss = false;
                yield return WaitForState(BehaviourState.Idle, 90f);
                break;

            case 6: // target stands still: stay in Follow, no drift away
                yield return Prepare(false);
                yield return WaitForState(BehaviourState.Hover, 20f);
                Operator(OperatorCommand.Follow);
                yield return WaitForState(BehaviourState.Follow, 15f);
                yield return HoldMeasured(15f);
                ok = _followRatio >= 0.6f && _maxDistance < 8f;
                detail = $"in Follow {_followRatio:P0} of 15 s, max distance {_maxDistance:F1} m";
                break;
        }

        if (behaviour.State != BehaviourState.Idle)
        {
            Operator(OperatorCommand.Land);
            drone.Land();
            yield return WaitForState(BehaviourState.Idle, 90f);
        }

        string result = $"[Scenario] {ScenarioNames[index]}: {(ok ? "PASS" : "FAIL")} ({detail})";
        _results.Add(result);
        Debug.Log(result);
        _running = "";
    }

    IEnumerator Prepare(bool behind)
    {
        behaviour.batteryOverride = -1f;
        behaviour.simulateLinkLoss = false;
        behaviour.forceTargetLost = false;
        target.gameObject.SetActive(true);
        walker.Stop();
        target.SetPositionAndRotation(_targetPosition, _targetRotation);
        Quaternion rotation = behind ? _spawnRotation * Quaternion.Euler(0f, 180f, 0f) : _spawnRotation;
        drone.ResetController(_spawnPosition, rotation);
        perception.Tracker.Reset();
        perception.autoLockTarget = true;
        yield return new WaitForSeconds(0.5f);
        behaviour.Request(OperatorCommand.Takeoff);
    }

    void StartWalking()
    {
        if (paths != null && paths.Length > 0)
            walker.Begin(paths[0], false, 0.8f);
    }

    void Operator(OperatorCommand command) => behaviour.Request(command);
    float Distance() => Vector3.Distance(drone.transform.position, target.position);

    IEnumerator WaitForState(BehaviourState state, float timeoutSeconds)
    {
        float end = Time.time + timeoutSeconds;
        while (behaviour.State != state && Time.time < end)
            yield return null;
    }

    float _followRatio;
    float _maxDistance;

    IEnumerator HoldMeasured(float seconds)
    {
        float end = Time.time + seconds, followTime = 0f;
        _maxDistance = 0f;
        float last = Time.time, nextSample = Time.time;
        while (Time.time < end)
        {
            yield return null;
            float dt = Time.time - last;
            last = Time.time;
            if (behaviour.State == BehaviourState.Follow)
                followTime += dt;
            _maxDistance = Mathf.Max(_maxDistance, Distance());
            if (Time.time >= nextSample)
            {
                nextSample += 5f;
                Debug.Log($"[Scenario] sample state={behaviour.State} distance={Distance():F1} m alt={drone.Navdata.altitude:F2} policy={behaviour.LastPolicy} walking={walker.IsWalking} visible={perception.Current.Visible}");
            }
        }
        _followRatio = followTime / seconds;
    }

    IEnumerator Hold(float seconds)
    {
        float end = Time.time + seconds;
        while (Time.time < end)
            yield return null;
    }

    // ───────────────────────── manual bench UI ─────────────────────────

    void OnGUI()
    {
        float unit = Mathf.Max(20f, Screen.height / 30f);
        _label ??= new GUIStyle(GUI.skin.label) { normal = { textColor = Color.white } };
        _button ??= new GUIStyle(GUI.skin.button);
        _label.fontSize = Mathf.RoundToInt(unit * 0.6f);
        _button.fontSize = Mathf.RoundToInt(unit * 0.6f);

        NavdataPacket nav = drone.Navdata;
        string text =
            $"[{behaviour.State}] {behaviour.Status}\n" +
            $"drone {drone.State} · alt {nav.altitude:F2} m · battery {nav.battery:F0}% (filtered {behaviour.BatteryFiltered:F0}%) · distance to target {Distance():F1} m\n" +
            $"target visible {perception.Current.Visible} · people {perception.PeopleInView} · {(string.IsNullOrEmpty(_running) ? "manual" : "scenario " + _running)}\n" +
            string.Join("\n", _results);
        GUI.Box(new Rect(unit * 0.3f, unit * 0.3f, Screen.width * 0.6f, unit * (3.6f + _results.Count * 0.9f)), GUIContent.none);
        GUI.Label(new Rect(unit * 0.5f, unit * 0.4f, Screen.width * 0.6f, unit * (3.5f + _results.Count)), text, _label);

        float width = Screen.width / 7f - unit * 0.3f;
        float y = Screen.height - unit * 4.6f;
        for (int i = 0; i < ScenarioNames.Length; i++)
            if (GUI.Button(new Rect(unit * 0.3f + i * (width + unit * 0.3f), y, width, unit * 1.8f), ScenarioNames[i], _button) && string.IsNullOrEmpty(_running))
                StartCoroutine(RunScenario(i));

        y += unit * 2.2f;
        string[] names = { "TAKEOFF (T)", "SEARCH (S)", "FOLLOW (F)", "HOVER (H)", "LAND (L)" };
        OperatorCommand[] commands = { OperatorCommand.Takeoff, OperatorCommand.Search, OperatorCommand.Follow, OperatorCommand.Hover, OperatorCommand.Land };
        for (int i = 0; i < names.Length; i++)
            if (GUI.Button(new Rect(unit * 0.3f + i * (width + unit * 0.3f), y, width, unit * 1.8f), names[i], _button))
                Operator(commands[i]);

        Event e = Event.current;
        if (e.type == EventType.KeyDown)
            switch (e.keyCode)
            {
                case KeyCode.T: Operator(OperatorCommand.Takeoff); break;
                case KeyCode.S: Operator(OperatorCommand.Search); break;
                case KeyCode.F: Operator(OperatorCommand.Follow); break;
                case KeyCode.H: Operator(OperatorCommand.Hover); break;
                case KeyCode.L: Operator(OperatorCommand.Land); break;
            }
    }
}
