using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class PersonTargetTrackerTests
{
    static readonly float[] Red = Signature(Color.red, Color.blue);
    static readonly float[] Green = Signature(Color.green, Color.yellow);

    [Test]
    public void TapLocksThePersonUnderTheFinger()
    {
        var tracker = new PersonTargetTracker();
        var people = Frame((0.2f, Red, 0.9f), (0.7f, Green, 0.5f));
        tracker.RequestLock(new Vector2(0.72f, 0.5f));

        Assert.AreEqual(1, tracker.Select(people, 0f, autoLock: false));
        Assert.IsTrue(tracker.HasLock);
    }

    [Test]
    public void WithoutLockNothingIsFollowedUnlessAutoLock()
    {
        var tracker = new PersonTargetTracker();
        var people = Frame((0.2f, Red, 0.4f), (0.7f, Green, 0.9f));

        Assert.AreEqual(-1, tracker.Select(people, 0f, autoLock: false));
        Assert.AreEqual(1, tracker.Select(people, 0f, autoLock: true), "auto lock takes the most confident person");
    }

    [Test]
    public void KeepsTheTargetWhenAMoreConfidentPersonAppears()
    {
        var tracker = new PersonTargetTracker();
        tracker.RequestLock(new Vector2(0.5f, 0.5f));
        tracker.Select(Frame((0.5f, Red, 0.5f)), 0f, false);

        int selected = tracker.Select(Frame((0.2f, Green, 0.99f), (0.52f, Red, 0.4f)), 0.1f, false);
        Assert.AreEqual(1, selected);
    }

    [Test]
    public void ReacquiresTheTargetByAppearanceAfterOcclusion()
    {
        var tracker = new PersonTargetTracker();
        tracker.RequestLock(new Vector2(0.5f, 0.5f));
        tracker.Select(Frame((0.5f, Red, 0.8f)), 0f, false);

        Assert.AreEqual(-1, tracker.Select(Frame((0.5f, Green, 0.9f)), 2f, false), "a stranger at the old spot is not the target");
        Assert.AreEqual(0, tracker.Select(Frame((0.1f, Red, 0.6f)), 2.1f, false), "the target reappears elsewhere");
    }

    [Test]
    public void SignatureSimilarityIsOneForIdenticalAndLowForDifferentClothes()
    {
        Assert.AreEqual(1f, PersonTargetTracker.HistogramSimilarity(Red, Red), 1e-4f);
        Assert.Less(PersonTargetTracker.HistogramSimilarity(Red, Green), 0.2f);
    }

    static List<PersonTargetTracker.Candidate> Frame(params (float centerX, float[] signature, float score)[] people)
    {
        var frame = new List<PersonTargetTracker.Candidate>();
        foreach (var person in people)
        {
            frame.Add(new PersonTargetTracker.Candidate
            {
                Box = new Rect(person.centerX - 0.05f, 0.3f, 0.1f, 0.4f),
                Score = person.score,
                Signature = person.signature
            });
        }
        return frame;
    }

    static float[] Signature(Color upper, Color lower)
    {
        const int width = 10, height = 20;
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[y * width + x] = y >= height / 2 ? upper : lower;  // bottom-up rows: upper body at the top
        return AppearanceSignature.Compute(pixels, width, height, new Rect(0f, 0f, 1f, 1f));
    }
}
