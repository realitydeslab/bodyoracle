using System;
using UnityEngine;

/// <summary>Pronounces automatically: fires when the shown person keeps the same character for
/// <see cref="holdSeconds"/>. The character just pronounced is read again only after the pose is held
/// for <see cref="repeatSeconds"/> more, counted from when polling resumes after the playback.</summary>
public sealed class PoseHold
{
    const float PollSeconds = 0.2f;   // one 48-byte readback of the centred track
    const float LostSeconds = 1f;     // nobody in the centre this long clears the memory
    readonly float holdSeconds, repeatSeconds;
    int heldClass = -1, spokenClass = -1;
    float heldSince, lastSeen, nextPoll, lastPoll;
    bool polling;

    public PoseHold(float holdSeconds, float repeatSeconds)
    {
        this.holdSeconds = holdSeconds;
        this.repeatSeconds = repeatSeconds;
    }

    /// <summary>A character has been recognised in the centre within the last poll or two.</summary>
    public bool HasCharacter => heldClass >= 0 && Time.unscaledTime - lastSeen < 3 * PollSeconds;

    /// <summary>Records a manual pronunciation so the same held pose is not read again.</summary>
    public void MarkSpoken(int classId)
    {
        spokenClass = heldClass = classId;
        heldSince = lastSeen = Time.unscaledTime;
    }

    /// <summary>Call every frame while nothing is playing; <paramref name="pronounce"/> runs on a hold.</summary>
    public void Update(Yolo detector, Action<DetectionData.PoseDetection> pronounce)
    {
        float now = Time.unscaledTime;
        if (polling || now < nextPoll || detector == null || !detector.HasFrame) return;
        // Polling stops while a pronunciation plays; a hold restarts when it resumes.
        if (now - lastPoll > 3 * PollSeconds) heldSince = now;
        lastPoll = now;
        nextPoll = now + PollSeconds;
        polling = detector.RequestCenterDetection((found, detection) =>
        {
            polling = false;
            float time = Time.unscaledTime;
            if (Debug.isDebugBuild)
                Debug.Log($"[PoseHold] found={found} class={(found ? detection.predictedClassId : -1)} held={heldClass} for {(heldClass >= 0 ? time - heldSince : 0):F1}s spoken={spokenClass}");
            if (!found)
            {
                if (time - lastSeen > LostSeconds) heldClass = spokenClass = -1;
                return;
            }
            lastSeen = time;
            if (detection.predictedClassId != heldClass)
            {
                heldClass = detection.predictedClassId;
                heldSince = time;
                if (heldClass != spokenClass) spokenClass = -1;
                return;
            }
            float needed = heldClass == spokenClass ? repeatSeconds : holdSeconds;
            if (time - heldSince >= needed)
            {
                spokenClass = heldClass;
                heldSince = time;
                pronounce(detection);
            }
        });
    }
}
