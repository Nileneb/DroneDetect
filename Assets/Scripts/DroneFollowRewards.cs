/// <summary>Pure reward terms for DroneFollow, kept free of Unity state so they can be unit-tested.</summary>
public static class DroneFollowRewards
{
    public const float MinimumVisibleFraction = 0.5f;
    public const float OutOfBandGateFraction = 0.4f;

    /// <summary>
    /// Gate pass value in [0, 1]: full when the person is in view now, was in view for most of the
    /// recent window, and is at follow distance; partial when only visibility holds.
    /// </summary>
    public static float GateReward(float visibleFraction, bool visibleNow, float distance, float min, float max)
    {
        // WHY(spec, anti-goal): a corner-cutting pass with the person lost must earn nothing, otherwise
        // the policy learns to fly gates instead of following.
        if (!visibleNow || visibleFraction < MinimumVisibleFraction)
            return 0f;
        return distance >= min && distance <= max ? 1f : OutOfBandGateFraction;
    }

    /// <summary>
    /// Distance error in metres outside the follow band; closer than <paramref name="tooCloseDistance"/>
    /// is weighted extra because the real drone would fly at the person.
    /// </summary>
    public static float DistanceError(float distance, float min, float max,
        float tooCloseDistance = 1.5f, float tooCloseWeight = 3f)
    {
        if (distance > max)
            return distance - max;
        if (distance >= min)
            return 0f;
        float error = min - distance;
        if (distance < tooCloseDistance)
            error += tooCloseWeight * (tooCloseDistance - distance);
        return error;
    }
}
