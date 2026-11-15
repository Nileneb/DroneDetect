using NUnit.Framework;

public sealed class DroneFollowRewardsTests
{
    [Test]
    public void GateReward_InBandAndVisible_IsOne()
    {
        Assert.AreEqual(1f, DroneFollowRewards.GateReward(1f, true, 2.5f, 2f, 3f), 1e-5f);
    }

    [Test]
    public void GateReward_VisibleOutOfBand_IsPartial()
    {
        Assert.AreEqual(0.4f, DroneFollowRewards.GateReward(0.8f, true, 5f, 2f, 3f), 1e-5f);
    }

    [Test]
    public void GateReward_NotVisible_IsZero()
    {
        Assert.AreEqual(0f, DroneFollowRewards.GateReward(1f, false, 2.5f, 2f, 3f), 1e-5f);
    }

    [Test]
    public void GateReward_MostlyLostBeforePass_IsZero()
    {
        Assert.AreEqual(0f, DroneFollowRewards.GateReward(0.3f, true, 2.5f, 2f, 3f), 1e-5f);
    }

    [Test]
    public void DistanceError_InBand_IsZero()
    {
        Assert.AreEqual(0f, DroneFollowRewards.DistanceError(2.4f, 2f, 3f), 1e-5f);
    }

    [Test]
    public void DistanceError_TooCloseIsWorseThanTooFar()
    {
        float tooClose = DroneFollowRewards.DistanceError(1f, 2f, 3f);
        float tooFar = DroneFollowRewards.DistanceError(4f, 2f, 3f);
        Assert.Greater(tooClose, tooFar);
        Assert.AreEqual(1f, tooFar, 1e-5f);
    }
}

public sealed class DroneFollowPathTests
{
    [Test]
    public void VisitOrder_LoopsBackToTheStartInBothDirections()
    {
        var root = new UnityEngine.GameObject("Path");
        var waypoints = new UnityEngine.GameObject(DroneFollowPath.WaypointRootName).transform;
        waypoints.SetParent(root.transform);
        for (int i = 0; i < 4; i++)
        {
            var waypoint = new UnityEngine.GameObject("W" + i).transform;
            waypoint.SetParent(waypoints);
            waypoint.position = new UnityEngine.Vector3(i, 0f, 0f);
        }

        var path = root.AddComponent<DroneFollowPath>();
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 0 }, path.VisitOrder(false));
        CollectionAssert.AreEqual(new[] { 0, 3, 2, 1, 0 }, path.VisitOrder(true));
        Assert.AreEqual(6f, path.Length, 1e-4f);
        UnityEngine.Object.DestroyImmediate(root);
    }
}
