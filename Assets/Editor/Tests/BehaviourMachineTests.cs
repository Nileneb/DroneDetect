using NUnit.Framework;
using UnityEngine;

public sealed class BehaviourMachineTests
{
    const float Dt = 0.1f;

    static BehaviourInputs Flying(DroneState state = DroneState.Hovering, bool target = false) => new BehaviourInputs
    {
        DroneState = state,
        Altitude = 1.2f,
        Battery = 80f,
        LinkUp = true,
        TargetVisible = target
    };

    static BehaviourOutput Run(BehaviourMachine machine, BehaviourInputs input, float seconds)
    {
        BehaviourOutput output = default;
        int ticks = Mathf.RoundToInt(seconds / Dt);
        for (int i = 0; i < ticks; i++)
        {
            output = machine.Tick(input, Dt);
            input.Operator = OperatorCommand.None;
        }
        return output;
    }

    static BehaviourMachine Airborne(out BehaviourInputs input)
    {
        var machine = new BehaviourMachine();
        input = new BehaviourInputs { DroneState = DroneState.Landed, LinkUp = true, Battery = 80f, Operator = OperatorCommand.Takeoff };
        var output = machine.Tick(input, Dt);
        Assert.IsTrue(output.RequestTakeoff);
        input = Flying();
        machine.Tick(input, Dt);
        Assert.AreEqual(BehaviourState.Hover, machine.State);
        return machine;
    }

    [Test]
    public void IdleIgnoresEverythingExceptTakeoff()
    {
        var machine = new BehaviourMachine();
        var input = new BehaviourInputs { DroneState = DroneState.Landed, LinkUp = true, Operator = OperatorCommand.Follow, TargetVisible = true };
        var output = Run(machine, input, 1f);
        Assert.AreEqual(BehaviourState.Idle, output.State);
        Assert.IsFalse(output.RequestMove);
        Assert.IsFalse(output.RequestLand);
    }

    [Test]
    public void TakeoffIsRefusedWithoutLink()
    {
        var machine = new BehaviourMachine();
        machine.Tick(new BehaviourInputs { DroneState = DroneState.Landed, LinkUp = false, Operator = OperatorCommand.Takeoff }, Dt);
        Assert.AreEqual(BehaviourState.Idle, machine.State);
    }

    [Test]
    public void TakeoffRequestsOnlyOnceAndNeverForwardsPolicy()
    {
        var machine = new BehaviourMachine();
        var input = new BehaviourInputs { DroneState = DroneState.Landed, LinkUp = true, Operator = OperatorCommand.Takeoff, Policy = new Vector4(1, 1, 1, 1), TargetVisible = true };
        Assert.IsTrue(machine.Tick(input, Dt).RequestTakeoff);
        input.Operator = OperatorCommand.None;
        input.DroneState = DroneState.TakingOff;
        var output = machine.Tick(input, Dt);
        Assert.IsFalse(output.RequestTakeoff);
        Assert.IsFalse(output.RequestMove);
        Assert.AreEqual(BehaviourState.Takeoff, output.State);
    }

    [Test]
    public void TakeoffTimeoutGoesToFailsafe()
    {
        var machine = new BehaviourMachine();
        var input = new BehaviourInputs { DroneState = DroneState.Landed, LinkUp = true, Battery = 80f, Operator = OperatorCommand.Takeoff };
        machine.Tick(input, Dt);
        input.Operator = OperatorCommand.None;
        input.DroneState = DroneState.TakingOff;
        var output = Run(machine, input, 9f);
        Assert.AreEqual(BehaviourState.Failsafe, output.State);
        Assert.IsTrue(output.RequestLand);
    }

    [Test]
    public void PolicyIsForwardedOnlyInFollow()
    {
        var machine = Airborne(out var input);
        input.Policy = new Vector4(0.5f, 0.1f, 0.2f, 0.3f);
        input.TargetVisible = true;
        Assert.IsFalse(Run(machine, input, 1f).RequestMove); // Hover

        input.Operator = OperatorCommand.Follow;
        var output = machine.Tick(input, Dt);
        Assert.AreEqual(BehaviourState.Follow, output.State);
        Assert.IsTrue(output.RequestMove);
        Assert.AreEqual(input.Policy, output.Move);
    }

    [Test]
    public void FollowWithoutTargetStartsSearch()
    {
        var machine = Airborne(out var input);
        input.Operator = OperatorCommand.Follow;
        Assert.AreEqual(BehaviourState.Search, machine.Tick(input, Dt).State);
    }

    [Test]
    public void FollowCapsClimbAtMaxAltitude()
    {
        var machine = Airborne(out var input);
        input.TargetVisible = true;
        input.Operator = OperatorCommand.Follow;
        input.Policy = new Vector4(0, 0, 0.8f, 0);
        input.Altitude = 2.6f;
        Assert.AreEqual(0f, machine.Tick(input, Dt).Move.z);
    }

    [Test]
    public void LostTargetFlowHoverThenSearchThenLand()
    {
        var machine = Airborne(out var input);
        input.TargetVisible = true;
        input.Operator = OperatorCommand.Follow;
        machine.Tick(input, Dt);
        input.Operator = OperatorCommand.None;

        input.TargetVisible = false;
        Assert.AreEqual(BehaviourState.Follow, Run(machine, input, 1.2f).State);
        Assert.AreEqual(BehaviourState.LostTarget, Run(machine, input, 0.6f).State);
        var hover = machine.Tick(input, Dt);
        Assert.IsFalse(hover.RequestMove);

        Assert.AreEqual(BehaviourState.Search, Run(machine, input, 1.6f).State); // lost for >3 s
        Assert.AreEqual(BehaviourState.Search, Run(machine, input, 10f).State);
        Assert.AreEqual(BehaviourState.Landing, Run(machine, input, 40f).State);  // auto search ends in landing
    }

    [Test]
    public void FollowStartsWithCleanLostClock()
    {
        var machine = Airborne(out var input);
        Run(machine, input, 20f); // long hover without target
        input.TargetVisible = true;
        input.Operator = OperatorCommand.Follow;
        var output = machine.Tick(input, Dt);
        Assert.AreEqual(BehaviourState.Follow, output.State);
        input.Operator = OperatorCommand.None;
        Assert.AreEqual(BehaviourState.Follow, Run(machine, input, 0.5f).State);
    }

    [Test]
    public void FlickeringDetectionsDoNotKeepFollowAlive()
    {
        var machine = Airborne(out var input);
        input.TargetVisible = true;
        input.Operator = OperatorCommand.Follow;
        Run(machine, input, 1f);
        input.Operator = OperatorCommand.None;
        // 0.1 s sighting every 0.5 s: never stable, so the target counts as lost after followLostSeconds.
        for (int i = 0; i < 25; i++)
        {
            input.TargetVisible = i % 5 == 0;
            machine.Tick(input, Dt);
        }
        Assert.AreEqual(BehaviourState.LostTarget, machine.State);
    }

    [Test]
    public void LostTargetReacquiredReturnsToFollow()
    {
        var machine = Airborne(out var input);
        input.TargetVisible = true;
        input.Operator = OperatorCommand.Follow;
        machine.Tick(input, Dt);
        input.Operator = OperatorCommand.None;
        input.TargetVisible = false;
        Run(machine, input, 2f);
        Assert.AreEqual(BehaviourState.LostTarget, machine.State);
        input.TargetVisible = true;
        Assert.AreEqual(BehaviourState.Follow, Run(machine, input, 0.7f).State);
    }

    [Test]
    public void OperatorSearchTimeoutHoversInsteadOfLanding()
    {
        var machine = Airborne(out var input);
        input.Operator = OperatorCommand.Search;
        machine.Tick(input, Dt);
        input.Operator = OperatorCommand.None;
        Assert.AreEqual(BehaviourState.Hover, Run(machine, input, 46f).State);
    }

    [Test]
    public void SearchTurnsThenClimbsInStepsWithinCeiling()
    {
        var settings = new BehaviourSettings();
        var machine = Airborne(out var input);
        input.Operator = OperatorCommand.Search;
        var output = machine.Tick(input, Dt);
        input.Operator = OperatorCommand.None;
        Assert.AreEqual(settings.searchYaw, output.Move.w, 1e-4);
        Assert.AreEqual(0f, output.Move.z);

        output = Run(machine, input, settings.SearchRevolutionSeconds + 0.3f);
        Assert.AreEqual(settings.searchClimb, output.Move.z, 1e-4); // climb step after one turn
        Assert.AreEqual(0f, output.Move.w);

        input.Altitude = settings.searchMaxAltitude;
        output = Run(machine, input, settings.searchClimbSeconds + 0.3f + settings.SearchRevolutionSeconds + 0.3f);
        Assert.AreEqual(0f, output.Move.z); // ceiling reached: keeps turning only
    }

    [Test]
    public void SearchNeedsStableTargetBeforeFollow()
    {
        var machine = Airborne(out var input);
        input.Operator = OperatorCommand.Search;
        machine.Tick(input, Dt);
        input.Operator = OperatorCommand.None;

        input.TargetVisible = true;
        Run(machine, input, 0.3f);
        input.TargetVisible = false;
        Run(machine, input, 0.1f);
        input.TargetVisible = true;
        Assert.AreEqual(BehaviourState.Search, Run(machine, input, 0.3f).State); // flicker is not enough
        Assert.AreEqual(BehaviourState.Follow, Run(machine, input, 0.4f).State);
    }

    [Test]
    public void OperatorLandBeatsEverythingAirborne()
    {
        var machine = Airborne(out var input);
        input.Operator = OperatorCommand.Land;
        var output = machine.Tick(input, Dt);
        Assert.AreEqual(BehaviourState.Landing, output.State);
        Assert.IsTrue(output.RequestLand);
        input.Operator = OperatorCommand.Follow;
        input.TargetVisible = true;
        Assert.AreEqual(BehaviourState.Landing, machine.Tick(input, Dt).State);
        input.DroneState = DroneState.Landed;
        Assert.AreEqual(BehaviourState.Idle, machine.Tick(input, Dt).State);
    }

    [Test]
    public void LinkLossLandsAfterTimeout()
    {
        var machine = Airborne(out var input);
        input.LinkUp = false;
        Assert.AreEqual(BehaviourState.Hover, Run(machine, input, 0.8f).State);
        var output = Run(machine, input, 0.5f);
        Assert.AreEqual(BehaviourState.Failsafe, output.State);
        Assert.AreEqual("link lost", machine.LastReason);
    }

    [Test]
    public void AppPauseIsFailsafe()
    {
        var machine = Airborne(out var input);
        input.AppPaused = true;
        Assert.AreEqual(BehaviourState.Failsafe, machine.Tick(input, Dt).State);
    }

    [Test]
    public void ShortBatterySagDoesNotLand()
    {
        var machine = Airborne(out var input);
        Run(machine, input, 3f);
        input.Battery = 5f; // sag for 1 s
        Run(machine, input, 1f);
        input.Battery = 80f;
        Assert.AreEqual(BehaviourState.Hover, Run(machine, input, 10f).State);
    }

    [Test]
    public void SustainedLowBatteryLands()
    {
        var machine = Airborne(out var input);
        input.Battery = 10f;
        var output = Run(machine, input, 10f);
        Assert.AreEqual(BehaviourState.Failsafe, output.State);
        StringAssert.Contains("battery", machine.LastReason);
    }

    [Test]
    public void EmergencyEndsInIdle()
    {
        var machine = Airborne(out var input);
        input.DroneState = DroneState.Emergency;
        Assert.AreEqual(BehaviourState.Idle, machine.Tick(input, Dt).State);
    }
}
