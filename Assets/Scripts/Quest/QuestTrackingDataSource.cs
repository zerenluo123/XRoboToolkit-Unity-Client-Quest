using System;
using System.Collections.Generic;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using Oculus.Interaction.Input.Visuals;
using UnityEngine;
using UnityEngine.Assertions;

public class QuestTrackingDataSource : MonoBehaviour
{
    public Hmd headset;
    public HandVisual leftHandVisual;
    public HandVisual rightHandVisual;

    public List<Transform> leftHandJoints;
    public List<Transform> rightHandJoints;

    public Controller leftController;
    public Controller rightController;
    public OVRControllerVisual leftControllerVisual;
    public OVRControllerVisual rightControllerVisual;

    /// <summary>
    /// Determines the currently active input device type based on what's being used
    /// </summary>
    /// <returns>2 for hand tracking, 1 for controller, 0 for headset only</returns>
    public int GetActiveInputDevice()
    {
        // handtracking 2
        // controller 1
        // headset 0
        if (leftHandVisual.IsVisible || rightHandVisual.IsVisible)
        {
            return 2; // handtracking
        }

        if (leftController.IsConnected || rightController.IsConnected)
        {
            return 1; // controller
        }

        return 0; // by default return headset
    }

    /// <summary>
    /// Gets the current pose of the VR headset
    /// </summary>
    /// <returns>Tuple containing validity flag and pose data</returns>
    public (bool, Pose) GetHeadsetPose()
    {
        if (headset.TryGetRootPose(out Pose pose))
        {
            return (true, pose);
        }

        return (false, Pose.identity);
    }

    /// <summary>
    /// Gets the pose of the specified controller
    /// </summary>
    /// <param name="handedness">Which controller to get pose for (Left or Right)</param>
    /// <returns>Pose of the specified controller</returns>
    public Pose GetControllerPose(Handedness handedness)
    {
        if (handedness == Handedness.Left)
        {
            var pos = leftControllerVisual.transform.position;
            var rot = leftControllerVisual.transform.rotation;
            return new Pose(pos, rot);
        }
        else
        {
            var pos = rightControllerVisual.transform.position;
            var rot = rightControllerVisual.transform.rotation;
            return new Pose(pos, rot);
        }
    }

    /// <summary>
    /// Checks if the specified controller is currently active and tracking
    /// </summary>
    /// <param name="handedness">Which controller to check (Left or Right)</param>
    /// <returns>True if controller is active, false otherwise</returns>
    public bool IsControllerActive(Handedness handedness)
    {
        if (handedness == Handedness.Left)
        {
            return leftController.IsPoseValid;
        }

        return rightController.IsPoseValid;
    }

    /// <summary>
    /// Checks if hand tracking is currently active for the specified hand
    /// </summary>
    /// <param name="handedness">Which hand to check (Left or Right)</param>
    /// <returns>True if hand tracking is active, false otherwise</returns>
    public bool IsHandTrackingActive(Handedness handedness)
    {
        if (handedness == Handedness.Left)
        {
            return leftHandVisual.IsVisible;
        }

        return rightHandVisual.IsVisible;
    }

    /// <summary>
    /// Gets all joint poses for the specified hand
    /// </summary>
    /// <param name="handedness">Which hand to get joint data for (Left or Right)</param>
    /// <param name="poses">Array to populate with joint poses</param>
    public void GetJoints(Handedness handedness, ref Pose[] poses)
    {
        var joints = handedness == Handedness.Left ? leftHandJoints : rightHandJoints;

        for (var i = 0; i < joints.Count; i++)
        {
            poses[i] = joints[i].GetPose();
        }
    }

    /// <summary>
    /// Body tracking (Meta Inside-Out Body Tracking, IOBT) source.
    ///
    /// Assign in the inspector, or leave null to auto-find at Start. Body tracking requires
    /// OVRManager > Quest Features > Body Tracking Support = Supported and
    /// Movement Tracking > Body Tracking Fidelity = High (Low is IK-only, not camera-measured).
    /// </summary>
    public OVRBody body;

    /// <summary>
    /// The tracking space transform, i.e. the origin that IOBT joint coordinates are relative to.
    ///
    /// Despite the name, OVRCameraRig is not about cameras: it is the root of the whole XR tracking
    /// space, and the camera is merely one of its children. Assign OVRCameraRig.trackingSpace here.
    /// Only needed for body tracking, which is why the other pose sources above do without it:
    /// arm posture is relative to the shoulders and independent of where the operator stands.
    /// </summary>
    public Transform trackingSpace;

    private void Start()
    {
        // Both are optional in the inspector so existing scenes keep working; fall back to a
        // one-time scene scan rather than doing this per frame at the 90 Hz data rate.
        if (body == null)
        {
            body = FindObjectOfType<OVRBody>();
        }

        if (trackingSpace == null)
        {
            var rig = FindObjectOfType<OVRCameraRig>();
            trackingSpace = rig != null ? rig.trackingSpace : null;
        }

        // Explicit, because both of these otherwise fail silently: body tracking simply produces
        // nothing, with no error anywhere in logcat to indicate why.
        if (body == null)
        {
            Debug.LogWarning("[QuestTrackingDataSource] no OVRBody in scene: body tracking will " +
                             "produce no data. Run BodyTrackingSetup.Configure.");
        }

        if (trackingSpace == null)
        {
            Debug.LogWarning("[QuestTrackingDataSource] no trackingSpace: body joint poses will " +
                             "be sent without the tracking-space origin, so locomotion cannot be " +
                             "reconstructed downstream.");
        }
    }

    /// <summary>
    /// True when body tracking has produced at least one valid frame.
    /// </summary>
    /// <remarks>
    /// Returns false whenever the headset is off the head, including hung around the neck.
    ///
    /// What fails is <c>ovrp_GetBodyState4</c> returning false. Nothing in the managed SDK gates
    /// on mount state -- OVRBody has no such reference -- so the decision is behind that
    /// DllImport, in the closed-source native plugin or the OS tracking service. It cannot be
    /// read, only measured, and the measurement is unambiguous: body data appears and disappears
    /// on the same sample where <c>sys.hmt.mounted</c> flips, across repeated don/doff cycles,
    /// while the app keeps XR focus, the cameras stay initialised and IOBT inference keeps
    /// running on the DSP. This is neither power management nor the proximity sensor setting:
    /// <c>proximity_sensor_enabled</c> was already 0 and made no difference, and the property
    /// cannot be written without root, so there is nothing to work around here.
    ///
    /// Controller poses are NOT gated this way, which is why controller teleop survives being
    /// hung round the neck and body tracking does not: sampling both against the property showed
    /// body flipping in lockstep with it across three don/doff cycles while controller poses kept
    /// moving for 40s+ off-head. Controllers carry their own IMU and are tracked optically by the
    /// still-running cameras, whereas body has to be inferred from images of the operator, and
    /// that inference is withheld -- not skipped -- while unmounted.
    ///
    /// The stay-awake keep-alive (<c>stay_on_while_plugged_in=7</c>, a plug-type bitmask of
    /// AC|USB|wireless) only prevents the display sleeping so the CPU and TCP link survive. It is
    /// a different failure: it was fully in effect during the measurements above.
    /// </remarks>
    public bool IsBodyTrackingActive()
    {
        return body != null && body.BodyState != null;
    }

    /// <summary>
    /// Gets the raw body tracking state, or null when unavailable.
    /// </summary>
    /// <remarks>
    /// Joint positions are expressed in tracking space, NOT world space, so they only describe
    /// posture. Recovering where the operator stands requires the tracking space pose as well
    /// (see <see cref="GetTrackingSpacePose"/>).
    /// </remarks>
    public OVRPlugin.BodyState? GetBodyState()
    {
        return body != null ? body.BodyState : null;
    }

    /// <summary>
    /// Gets the tracking space pose in world coordinates.
    /// </summary>
    /// <remarks>
    /// Required to reconstruct root locomotion (walking around the room): joint coordinates are
    /// tracking-space local, so posture alone cannot tell where the operator is.
    /// </remarks>
    public Pose GetTrackingSpacePose()
    {
        if (trackingSpace == null)
        {
            return Pose.identity;
        }

        return new Pose(trackingSpace.position, trackingSpace.rotation);
    }
}