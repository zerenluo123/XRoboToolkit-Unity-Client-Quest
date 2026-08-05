using System;
using LitJson;
using UnityEngine;
using UnityEngine.XR;
// using UnityEngine.XR.Hands;
using UnityEngine.XR.Management;
using Unity.Collections;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using CommonUsages = UnityEngine.XR.CommonUsages;
using InputDevice = UnityEngine.XR.InputDevice;

namespace Robot
{
    public class TrackingData
    {
        public static bool HeadOn { get; private set; }
        public static bool ControllerOn { get; private set; }
        public static bool HandTrackingOn { get; private set; }
        public static TrackingType TrackingTypeValue { get; private set; }

        private JsonData _bodyTrackingJson = new JsonData();

        private JsonData _controllerDataJson = new JsonData();
        private JsonData _leftControllerJson = new JsonData();
        private JsonData _rightControllerJson = new JsonData();

        private JsonData _stateData = new JsonData();
        private JsonData _handData = new JsonData();
        private JsonData _leftHandData = new JsonData();
        private JsonData _rightHandData = new JsonData();

        private QuestTrackingDataSource _questTrackingDataSource;

        public QuestTrackingDataSource questTrackingDataSource
        {
            get
            {
                if (_questTrackingDataSource == null)
                {
                    _questTrackingDataSource = GameObject.FindObjectOfType<QuestTrackingDataSource>();
                }

                return _questTrackingDataSource;
            }
        }

        /// <summary>
        /// The scene's tracking data source, for callers that hold no TrackingData instance.
        /// </summary>
        /// <remarks>
        /// The UI needs the same body state that gets published, to show calibration progress.
        /// TrackingData is instantiated by TcpHandler and not otherwise reachable, so rather than
        /// have the UI run its own FindObjectOfType this exposes the one cached lookup. There is
        /// only ever one source in the scene, so caching statically is safe.
        /// </remarks>
        public static QuestTrackingDataSource SharedQuestTrackingDataSource
        {
            get
            {
                if (_sharedQuestTrackingDataSource == null)
                {
                    _sharedQuestTrackingDataSource =
                        GameObject.FindObjectOfType<QuestTrackingDataSource>();
                }

                return _sharedQuestTrackingDataSource;
            }
        }

        private static QuestTrackingDataSource _sharedQuestTrackingDataSource;

        /// <summary>
        /// Sets whether head tracking is enabled
        /// </summary>
        /// <param name="on">True to enable head tracking, false to disable</param>
        public static void SetHeadOn(bool on)
        {
            HeadOn = on;
        }

        /// <summary>
        /// Sets whether controller tracking is enabled
        /// </summary>
        /// <param name="on">True to enable controller tracking, false to disable</param>
        public static void SetControllerOn(bool on)
        {
            ControllerOn = on;
        }

        /// <summary>
        /// Sets whether hand tracking is enabled
        /// </summary>
        /// <param name="on">True to enable hand tracking, false to disable</param>
        public static void SetHandTrackingOn(bool on)
        {
            HandTrackingOn = on;
        }

        /// <summary>
        /// Sets the current tracking type (None, Body, FullBody)
        /// </summary>
        /// <param name="trackingType">The tracking type to set</param>
        public static void SetTrackingType(TrackingType trackingType)
        {
            TrackingTypeValue = trackingType;
        }

        public static bool HasTracking
        {
            get
            {
                return (HeadOn || ControllerOn || HandTrackingOn ||
                        TrackingTypeValue > TrackingType.None);
            }
        }

        /// <summary>
        /// Gathers all tracking data and populates the provided JsonData with current sensor information
        /// Includes head, controller, and hand tracking data based on enabled features
        /// </summary>
        /// <param name="totalData">The JsonData object to populate with tracking information</param>
        public void Get(ref JsonData totalData)
        {
            try
            {
                //sensor
                double predictTime = Time.unscaledTimeAsDouble * 1000000; // Convert to microseconds
                totalData["predictTime"] = predictTime; //微秒，对应camera录制中帧插入的时间戳
                totalData["appState"] = _stateData;
                _stateData["focus"] = Application.isFocused;

                if (HeadOn)
                {
                    JsonData sensorJson = GetHeadTrackingData();
                    totalData["Head"] = sensorJson;
                }
                else
                {
                    if (totalData.ContainsKey("Head"))
                        totalData.Remove("Head");
                }

                if (ControllerOn)
                {
                    JsonData controller = GetLeftRightControllerJsonData(predictTime);
                    totalData["Controller"] = controller;
                }
                else
                {
                    if (totalData.ContainsKey("Controller"))
                        totalData.Remove("Controller");
                }

                if (HandTrackingOn)
                {
                    GetHandJsonData();
                    totalData["Hand"] = _handData;
                }
                else
                {
                    if (totalData.ContainsKey("Hand"))
                        totalData.Remove("Hand");
                }

                // Published under "BodyMeta" rather than "Body": the latter is a fixed 24-joint
                // layout with per-joint velocity and acceleration, none of which Meta IOBT
                // provides. See Docs/BodyMeta.md for the format.
                if (JointSetOf(TrackingTypeValue) != null)
                {
                    GetBodyMetaJsonData();
                    totalData["BodyMeta"] = _bodyTrackingJson;
                }
                else
                {
                    // totalData is reused across frames, so a stale key would keep being sent.
                    if (totalData.ContainsKey("BodyMeta"))
                        totalData.Remove("BodyMeta");
                }

                long nsTime = Utils.GetCurrentTimestamp();
                totalData["timeStampNs"] = nsTime;

                // For OpenXR, we determine active input device based on what's available
                int inputDevice = GetActiveInputDevice(); //1
                totalData["Input"] = inputDevice;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"TrackingData.Get() failed: {e.StackTrace}");
                // Initialize minimal data structure to prevent further crashes
                if (!totalData.ContainsKey("predictTime"))
                    totalData["predictTime"] = Time.unscaledTimeAsDouble * 1000000;
                if (!totalData.ContainsKey("timeStampNs"))
                    totalData["timeStampNs"] = System.DateTime.UtcNow.Ticks;
                if (!totalData.ContainsKey("Input"))
                    totalData["Input"] = 0;
            }
        }
        
        /// <summary>
        /// Converts coordinate system handedness by flipping Z-axis and adjusting rotation
        /// Unity uses left-handed coordinates, but output needs right-handed coordinates,
        /// but the output needs to match PICO's right-handed (the same as OpenXR and Robot) system.
        /// This method can convert left-handed coordinates to right-handed, and vice versa.
        /// </summary>
        /// <param name="position">Position vector to convert</param>
        /// <param name="rotation">Rotation quaternion to convert</param>
        void ConvertHandedness(ref Vector3 position, ref Quaternion rotation)
        {
            position.z *= -1;
            rotation.z *= -1;
            rotation.w *= -1;
        }

        /// <summary>
        /// Gets head tracking data including pose and status information
        /// </summary>
        /// <returns>JsonData containing head pose and tracking status</returns>
        private JsonData GetHeadTrackingData()
        {
            JsonData jsonData = new JsonData();

            var (valid, pose) = questTrackingDataSource.GetHeadsetPose();
            if (valid)
            {
                var position = pose.position;
                var rotation = pose.rotation;
                ConvertHandedness(ref position, ref rotation);
                jsonData["pose"] = GetPoseStr(position, rotation);
                jsonData["status"] = 3; // Match PICO
            }
            else
            {
                jsonData["status"] = 0;
            }

            return jsonData;
        }

        /// <summary>
        /// Gets the currently active input device type
        /// </summary>
        /// <returns>Integer representing the active input device</returns>
        private int GetActiveInputDevice()
        {
            return questTrackingDataSource.GetActiveInputDevice();
        }

        /// <summary>
        /// Gets controller tracking data for the specified hand
        /// </summary>
        /// <param name="predictTime">Prediction time for the controller data</param>
        /// <param name="handedness">Which hand controller to get data for (Left or Right)</param>
        /// <returns>JsonData containing controller pose and button states</returns>
        private JsonData GetControllerJsonData(double predictTime, Handedness handedness)
        {
            InputDevice controller;
            var pose = Pose.identity;
            if (handedness == Handedness.Left)
            {
                controller = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                pose = questTrackingDataSource.GetControllerPose(Handedness.Left);
            }
            else
            {
                controller = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                pose = questTrackingDataSource.GetControllerPose(Handedness.Right);
            }

            // Debug.Log("Handedness: " + handedness + ", Controller: " + controller.name + ", Pose: " + pose);

            var json = new JsonData();
            GetControllerJsonData(controller, ref json);

            var position = pose.position;
            var rotation = pose.rotation;
            ConvertHandedness(ref position, ref rotation);
            json["pose"] = GetPoseStr(position, rotation);
            return json;
        }

        /// <summary>
        /// Gets tracking data for both left and right controllers
        /// </summary>
        /// <param name="predictTime">Prediction time for the controller data</param>
        /// <returns>JsonData containing both left and right controller data</returns>
        private JsonData GetLeftRightControllerJsonData(double predictTime)
        {
            _leftControllerJson = GetControllerJsonData(predictTime, Handedness.Left);
            _controllerDataJson["left"] = _leftControllerJson;

            _rightControllerJson = GetControllerJsonData(predictTime, Handedness.Right);
            _controllerDataJson["right"] = _rightControllerJson;

            return _controllerDataJson;
        }

        /// <summary>
        /// Extracts button and axis data from a controller device and populates JsonData
        /// </summary>
        /// <param name="controllerDevice">The input device to read from</param>
        /// <param name="json">The JsonData object to populate with controller input data</param>
        private static void GetControllerJsonData(InputDevice controllerDevice, ref JsonData json)
        {
            controllerDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis2D);
            controllerDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out var axisClick);
            controllerDevice.TryGetFeatureValue(CommonUsages.grip, out var grip);
            controllerDevice.TryGetFeatureValue(CommonUsages.trigger, out var trigger);
            controllerDevice.TryGetFeatureValue(CommonUsages.primaryButton, out var primaryButton);
            controllerDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out var secondaryButton);
            controllerDevice.TryGetFeatureValue(CommonUsages.menuButton, out var menuButton);

            json["axisX"] = axis2D.x;
            json["axisY"] = axis2D.y;
            json["axisClick"] = axisClick;
            json["grip"] = grip;
            json["trigger"] = trigger;
            json["primaryButton"] = primaryButton;
            json["secondaryButton"] = secondaryButton;
            json["menuButton"] = menuButton;
            // Pose will be set by the QuestTrackingDataSource
            // json["pose"] = GetPoseStr(position, rotation);
        }


        /// <summary>
        /// Gathers hand tracking data for both left and right hands
        /// </summary>
        private void GetHandJsonData()
        {
            // Clear previous data
            _handData = new JsonData();

            _leftHandData = new JsonData();
            GetOVRHandTrackingData(Handedness.Left, ref _leftHandData);
            _handData["leftHand"] = _leftHandData;

            _rightHandData = new JsonData();
            GetOVRHandTrackingData(Handedness.Right, ref _rightHandData);
            _handData["rightHand"] = _rightHandData;
        }

        /// <summary>
        /// Builds the BodyMeta JSON payload from Meta IOBT body tracking.
        /// </summary>
        /// <remarks>
        /// Every tracked joint is forwarded losslessly, tagged with its raw OVRPlugin.BoneId.
        /// Sending the id rather than relying on array order makes the payload self-describing:
        /// consumers resolve semantics against OVRPlugin.cs instead of guessing an index mapping.
        /// This matters because the UpperBody and FullBody skeletons use different id layouts
        /// (e.g. Body_LeftHandWrist = Body_Start+19 while Body_RightHandWrist = Body_Start+45),
        /// so "jointSet" must be read before interpreting any id.
        ///
        /// Velocity/acceleration are absent by design: Meta's BodyJointLocation carries only
        /// LocationFlags and Pose, unlike PICO's IMU trackers. Differentiate downstream if needed.
        /// </remarks>
        private void GetBodyMetaJsonData()
        {
            var state = questTrackingDataSource.GetBodyState();
            var isActive = state != null && state.Value.JointLocations != null;

            _bodyTrackingJson["isActive"] = isActive ? 1U : 0U;

            // The tracking space pose is what makes root locomotion recoverable: joint coordinates
            // are tracking-space local, so posture alone cannot say where the operator stands.
            var trackingSpacePose = questTrackingDataSource.GetTrackingSpacePose();
            var tsPosition = trackingSpacePose.position;
            var tsRotation = trackingSpacePose.rotation;
            ConvertHandedness(ref tsPosition, ref tsRotation);
            _bodyTrackingJson["trackingSpace"] = GetPoseStr(tsPosition, tsRotation);

            // JsonData objects are reused across frames rather than reallocated, matching the
            // PICO client. At the 90 Hz send rate a fresh object per joint would mean ~70
            // allocations every frame, which is avoidable GC churn on a mobile SoC.
            JsonData jointsJson;
            if (_bodyTrackingJson.ContainsKey("joints"))
            {
                jointsJson = _bodyTrackingJson["joints"];
            }
            else
            {
                jointsJson = new JsonData();
                jointsJson.SetJsonType(JsonType.Array);
                _bodyTrackingJson["joints"] = jointsJson;
            }

            if (!isActive)
            {
                _bodyTrackingJson["count"] = 0;
                return;
            }

            var bodyState = state.Value;
            var joints = bodyState.JointLocations;

            // Data-quality metadata, passed through for the consumer to interpret rather than
            // acted on here. Joints are published regardless of calib, matching Unity-Movement's
            // sample scene, which drives its avatar throughout Calibrating with no visible difference
            // once Valid arrives. Calibration lives in the runtime: it cannot be disabled or
            // pre-seeded, and it restarts on every re-don, so gating on it would stall the stream
            // for 30-60s each time for no gain. Calibrating means the runtime is still adjusting
            // skeleton scale, not that there is no data.
            _bodyTrackingJson["jointSet"] = bodyState.JointSet.ToString();
            _bodyTrackingJson["fidelity"] = bodyState.Fidelity.ToString();
            _bodyTrackingJson["calib"] = bodyState.CalibrationStatus.ToString();
            _bodyTrackingJson["confidence"] = bodyState.Confidence;
            _bodyTrackingJson["count"] = joints.Length;

            // Drop entries the current joint set does not have. The array is reused across
            // frames, and the loop below only overwrites the first joints.Length of them, so
            // switching FullBody -> UpperBody would otherwise leave the 14 leg joints behind
            // holding their last FullBody coordinates. A consumer sees an 84-entry array
            // disagreeing with count=70, and the stale joints look like tracking that froze
            // rather than a joint set that shrank.
            // Cast for RemoveAt: LitJson implements it explicitly via IList. Removing by
            // index rather than by value, since Remove(object) searches by equality and two
            // joints sharing a pose would delete the wrong entry.
            var jointsList = (System.Collections.IList)jointsJson;
            while (jointsList.Count > joints.Length)
            {
                jointsList.RemoveAt(jointsList.Count - 1);
            }

            for (var i = 0; i < joints.Length; i++)
            {
                var joint = joints[i];

                // Two distinct conversions in sequence, not a redundant double flip:
                //   FromFlippedZ*     : OVRPlugin (OpenXR, right-handed) -> Unity (left-handed)
                //   ConvertHandedness : Unity -> the wire convention shared with the PICO client
                // The hand path only needs the second one because Oculus.Interaction hands its
                // Pose over already converted; raw BodyState joints are pre-conversion.
                var position = joint.Pose.Position.FromFlippedZVector3f();
                var rotation = joint.Pose.Orientation.FromFlippedZQuatf();

                ConvertHandedness(ref position, ref rotation);

                JsonData jointJson;
                if (i < jointsJson.Count)
                {
                    jointJson = jointsJson[i];
                }
                else
                {
                    jointJson = new JsonData();
                    jointsJson.Add(jointJson);
                }

                jointJson["id"] = i;
                jointJson["p"] = GetPoseStr(position, rotation);
                // Occluded joints keep stale values, so validity must travel with the data
                jointJson["v"] = joint.PositionValid ? 1U : 0U;
                jointJson["vr"] = joint.OrientationValid ? 1U : 0U;
            }
        }

        /// <summary>
        /// Gets hand tracking data for a specific hand using OVR hand tracking system
        /// </summary>
        /// <param name="handedness">Which hand to get data for (Left or Right)</param>
        /// <param name="json">JsonData object to populate with hand tracking information</param>
        private void GetOVRHandTrackingData(Handedness handedness, ref JsonData json)
        {
            var isActive = questTrackingDataSource.IsHandTrackingActive(handedness);

            json["isActive"] = isActive ? 1U : 0U;
            json["count"] = 26; // Total number of bones
            json["scale"] = 1.0f;

            JsonData jointLocationsJson = new JsonData();
            jointLocationsJson.SetJsonType(JsonType.Array);
            json["HandJointLocations"] = jointLocationsJson;

            Pose[] joints = new Pose[26]; // to be global variable

            questTrackingDataSource.GetJoints(handedness, ref joints);
            float status = isActive ? 15.0f : 0.0f;

            foreach (var joint in joints)
            {
                var position = joint.position;
                var rotation = joint.rotation;

                ConvertHandedness(ref position, ref rotation);

                JsonData jointJson = new JsonData();

                jointJson["p"] = GetPoseStr(position, rotation);

                jointJson["s"] = status;

                // Comply with PICO 4U output
                jointJson["r"] = 0.0f;

                jointLocationsJson.Add(jointJson);
            }
        }

        // /// <summary>
        // /// Maps OVRPlugin hand status to OpenXR-compatible status flags
        // /// </summary>
        // private uint GetOVRTrackingStatus(OVRPlugin.HandStatus handStatus)
        // {
        //     uint status = 0;
        //
        //     // Bit layout (semantic mapping based on HandLocationStatus):
        //     // Bit 0: Position valid           => 0x01
        //     // Bit 1: Position tracked         => 0x02
        //     // Bit 2: Orientation valid        => 0x04
        //     // Bit 3: Orientation tracked      => 0x08
        //
        //     if (handStatus == OVRPlugin.HandStatus.HandTracked)
        //     {
        //         status |= 0x01; // Position valid
        //         status |= 0x02; // Position tracked
        //         status |= 0x04; // Orientation valid
        //         status |= 0x08; // Orientation tracked
        //     }
        //
        //     return status;
        // }

        // /// <summary>
        // /// Provides default radius values for OVRPlugin bone types
        // /// </summary>
        // private float GetDefaultJointRadiusForOVRBone(OVRPlugin.BoneId boneId)
        // {
        //     // Default radii based on typical hand joint sizes (in meters)
        //     switch (boneId)
        //     {
        //         case OVRPlugin.BoneId.Hand_WristRoot:
        //             return 0.025f;
        //
        //         // Thumb joints
        //         case OVRPlugin.BoneId.Hand_Thumb0:
        //         case OVRPlugin.BoneId.Hand_Thumb1:
        //         case OVRPlugin.BoneId.Hand_Thumb2:
        //         case OVRPlugin.BoneId.Hand_Thumb3:
        //             return 0.012f;
        //
        //         // Index finger joints
        //         case OVRPlugin.BoneId.Hand_Index1:
        //         case OVRPlugin.BoneId.Hand_Index2:
        //         case OVRPlugin.BoneId.Hand_Index3:
        //             return 0.008f;
        //
        //         // Middle finger joints
        //         case OVRPlugin.BoneId.Hand_Middle1:
        //         case OVRPlugin.BoneId.Hand_Middle2:
        //         case OVRPlugin.BoneId.Hand_Middle3:
        //             return 0.008f;
        //
        //         // Ring finger joints
        //         case OVRPlugin.BoneId.Hand_Ring1:
        //         case OVRPlugin.BoneId.Hand_Ring2:
        //         case OVRPlugin.BoneId.Hand_Ring3:
        //             return 0.007f;
        //
        //         // Little finger joints
        //         case OVRPlugin.BoneId.Hand_Pinky0:
        //         case OVRPlugin.BoneId.Hand_Pinky1:
        //         case OVRPlugin.BoneId.Hand_Pinky2:
        //         case OVRPlugin.BoneId.Hand_Pinky3:
        //             return 0.006f;
        //
        //         default:
        //             return 0.01f; // Generic default
        //     }
        // }

        // /// <summary>
        // /// Provides default radius values for different joint types based on OpenXR recommendations
        // /// </summary>
        // private float GetDefaultJointRadius(XRHandJointID jointID)
        // {
        //     // Default radii based on typical hand joint sizes (in meters)
        //     switch (jointID)
        //     {
        //         case XRHandJointID.Wrist:
        //             return 0.025f;
        //         case XRHandJointID.Palm:
        //             return 0.03f;
        //
        //         // Thumb joints
        //         case XRHandJointID.ThumbMetacarpal:
        //         case XRHandJointID.ThumbProximal:
        //         case XRHandJointID.ThumbDistal:
        //         case XRHandJointID.ThumbTip:
        //             return 0.012f;
        //
        //         // Index finger joints
        //         case XRHandJointID.IndexMetacarpal:
        //         case XRHandJointID.IndexProximal:
        //         case XRHandJointID.IndexIntermediate:
        //         case XRHandJointID.IndexDistal:
        //         case XRHandJointID.IndexTip:
        //             return 0.008f;
        //
        //         // Middle finger joints
        //         case XRHandJointID.MiddleMetacarpal:
        //         case XRHandJointID.MiddleProximal:
        //         case XRHandJointID.MiddleIntermediate:
        //         case XRHandJointID.MiddleDistal:
        //         case XRHandJointID.MiddleTip:
        //             return 0.008f;
        //
        //         // Ring finger joints
        //         case XRHandJointID.RingMetacarpal:
        //         case XRHandJointID.RingProximal:
        //         case XRHandJointID.RingIntermediate:
        //         case XRHandJointID.RingDistal:
        //         case XRHandJointID.RingTip:
        //             return 0.007f;
        //
        //         // Little finger joints
        //         case XRHandJointID.LittleMetacarpal:
        //         case XRHandJointID.LittleProximal:
        //         case XRHandJointID.LittleIntermediate:
        //         case XRHandJointID.LittleDistal:
        //         case XRHandJointID.LittleTip:
        //             return 0.006f;
        //
        //         default:
        //             return 0.01f; // Generic default
        //     }
        // }


        /// <summary>
        /// Converts position and rotation data into a comma-separated string format
        /// </summary>
        /// <param name="position">3D position vector</param>
        /// <param name="rotation">Rotation quaternion</param>
        /// <returns>String representation of pose in format: x,y,z,qx,qy,qz,qw</returns>
        private static string GetPoseStr(Vector3 position, Quaternion rotation)
        {
            return position.x.ToString("R") + "," + position.y.ToString("R") + "," + position.z.ToString("R") + "," +
                   rotation.x.ToString("R") + "," + rotation.y.ToString("R") + "," + rotation.z.ToString("R") + "," +
                   rotation.w.ToString("R");
        }

        public enum HandMode
        {
            Non = 0,
            Controller = 1,
            Hand = 2
        }

        public enum TrackingType
        {
            None = 0,

            /// <summary>Meta IOBT upper body: 70 camera-derived joints.</summary>
            Body = 1,

            /// <summary>The 70 above plus 14 lower-body joints from Generative Legs.</summary>
            /// <remarks>
            /// The legs are inferred from head and upper-body motion, not seen: the headset
            /// cameras cannot observe them while it is worn. Consumers that need measured joints
            /// only should stay on <see cref="Body"/>.
            /// </remarks>
            FullBody = 2
        }

        /// <summary>
        /// The joint set a mode publishes, or null when it publishes no body data.
        /// </summary>
        /// <remarks>
        /// One switch answers both "should we publish" and "which layout", because they are the
        /// same question -- adding a further body mode means adding one case here.
        /// </remarks>
        public static OVRPlugin.BodyJointSet? JointSetOf(TrackingType trackingType)
        {
            switch (trackingType)
            {
                case TrackingType.Body: return OVRPlugin.BodyJointSet.UpperBody;
                case TrackingType.FullBody: return OVRPlugin.BodyJointSet.FullBody;
                default: return null;
            }
        }
    }
}