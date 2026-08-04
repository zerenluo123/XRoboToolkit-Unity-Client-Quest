# BodyMeta — Quest body tracking wire format

Quest body tracking is published under its own top-level key, **`BodyMeta`**, rather than reusing
PICO's `Body`. This document explains why, and specifies the format for anyone writing a consumer.

## Why a separate key

| | PICO `Body` | Quest `BodyMeta` |
|---|---|---|
| Source | IMU motion trackers (external hardware) | Meta IOBT — the headset's own cameras |
| Joints | 24, fixed | 70 (`UpperBody`) or 84 (`FullBody`) |
| Per-joint data | pose + velocity + acceleration + per-joint IMU timestamp | pose + two validity flags |
| Calibration | app supplies `BodyTrackingBoneLength` | runs inside the runtime, cannot be pre-seeded |

The two are not interchangeable. Reusing `Body` would mean either silently truncating 70 joints into
24 slots, or redefining a key that existing PICO consumers already parse — so Quest gets its own key
and old consumers are unaffected. A consumer that sees `BodyMeta` knows it is talking to a Quest and
gets the joint layout from the `jointSet` field rather than assuming one.

Velocity and acceleration are absent because `OVRPlugin.BodyJointLocation` carries only
`LocationFlags` and `Pose` — there is nothing to report. Differentiate positions downstream if you
need rates.

## Format

`BodyMeta` is present only while **Mode = Body (IOBT)** is selected; it is removed from the packet
otherwise.

```json
{
  "timeStampNs": 1785486843349566208,
  "BodyMeta": {
    "isActive": 1,
    "count": 70,
    "jointSet": "UpperBody",
    "fidelity": "High",
    "calib": "Valid",
    "confidence": 1.0,
    "trackingSpace": "0.000,0.000,0.000,0.000,0.000,0.000,1.000",
    "joints": [
      { "id": 0, "p": "0.012,0.934,-0.048,0.001,0.707,0.002,0.707", "v": 1, "vr": 1 }
    ]
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `isActive` | int | `1` when tracking is live. **Check this first** — see below. |
| `count` | int | Number of entries in `joints`. `0` when inactive. |
| `jointSet` | string | `UpperBody` (70) or `FullBody` (84). Selects the consumer's joint-id table. |
| `fidelity` | string | `High` = camera-measured IOBT; `Low` = IK-only inference. |
| `calib` | string | `Valid` / `Calibrating` / `Invalid`. Informational, not a gate — see below. |
| `confidence` | float | Runtime's overall confidence, `0.0`–`1.0`. |
| `trackingSpace` | string | Tracking space's own pose in the world frame, same 7-component format as `p`. |
| `joints[].id` | int | Index into the joint set. |
| `joints[].p` | string | `x,y,z,qx,qy,qz,qw` — position in metres, rotation as a quaternion. |
| `joints[].v` | int | `PositionValid`. `0` when the joint is occluded. |
| `joints[].vr` | int | `OrientationValid`. Fails independently of `v`. |

`timeStampNs` is at the **top level**, not inside `BodyMeta`: every source in a frame shares one
timestamp, which is what makes headset pose and body joints alignable.

Coordinates are in the same left-handed wire convention as the rest of the client's data, so a
consumer needs no Quest-specific transform. Walking is already included — the tracking space is
fixed to the room, so joint positions carry the operator's translation.

## Two things consumers must know

**Check `isActive` before reading `joints`.** When the headset leaves the head, tracking stops but
the `joints` array **keeps its last values** — the JSON objects are reused rather than cleared, and
`calib`/`count` go stale too. Nothing in the numbers themselves reveals this, so a consumer that
skips the flag will act on a frozen pose.

Body tracking requires the headset actually worn; hanging it on the neck stops the data. The gate is
the system's mount detection, not power management: with the headset off, the cameras and IOBT
inference keep running but the runtime stops handing results to applications. Controller poses are
not gated this way, so controller teleop behaves differently here.

**Do not wait for `calib == "Valid"`.** Joints stream complete from the first frame. Calibration runs
inside the runtime, refining the skeleton's *scale*; `Valid` only means it stopped refining. Measured
on a Quest 3:

```
    t   isActive  calib          count
 61.5          1  Calibrating       70    <- put on: immediately 70 joints
 73.9          1  Valid             70    <- 12 s later it turns Valid
 78.0          0  Valid             70    <- taken off: stale values, isActive says so
```

All 70 joints flowed for 12 s before `Valid` arrived, and Unity-Movement's sample scene likewise
drives its avatar throughout `Calibrating` with no visible change when it settles. Calibration
cannot be disabled or pre-seeded (`BodyTrackingCalibrationInfo` exposes only `BodyHeight`, and
`SuggestBodyTrackingCalibrationOverride` is a suggestion the runtime may ignore) and it restarts on
every re-don, so gating on it would stall the stream for 30–60 s each time for no gain.

Skeleton proportions come from the runtime's defaults. Retargeting generally consumes angles and
directions, with absolute limb lengths normalised away; if you do need them, subtract joint
positions — measured across 69 bones, 62 varied by under 5 mm, with left/right symmetric to the
hundredth of a centimetre.

## Joint layout (`UpperBody`, 70 joints)

| id range | count | contents |
|---|---|---|
| `0 – 7` | 8 | Root, Hips, SpineLower, SpineMiddle, SpineUpper, Chest, Neck, Head |
| `8 – 17` | 10 | per side: Shoulder, Scapula, ArmUpper, ArmLower, HandWristTwist |
| `18 – 43` | 26 | left hand: Palm, Wrist, thumb (4) + 4 fingers (5 each) |
| `44 – 69` | 26 | right hand: same layout |

Note `id 44` is `RightHandPalm` — the right hand starts at 44, not 45.

For joint names, use `BodyJointId` from `BodyPrimitives.cs`, **not**
`OVRPlugin.BoneId.ToString()`: `BoneId` overlays several skeletons on the same numeric values, so it
reports hand joint names for body joints.

Parent indices are only available at runtime via `OVRPlugin.GetSkeleton2()`; there is no static table
in the SDK source.

## Project settings this depends on

Body tracking fails **silently** if any of these is wrong — no error, no log, just no data.
`Assets/Editor/BodyTrackingSetup.cs` applies them all and can be re-run at any time
(`Tools > Body Tracking > Configure Project Settings`).

| Setting | Location | Required value |
|---|---|---|
| `bodyTrackingSupport` | `OculusProjectConfig` | `1` |
| `bodyTrackingFidelity` | `OculusRuntimeSettings` | `2` (High) — `1` silently degrades to IK-only |
| `bodyTrackingJointSet` | `OculusRuntimeSettings` | `0` (UpperBody) |
| `requestBodyTrackingPermissionOnStartup` | `OculusProjectConfig` | `true` — otherwise `com.oculus.permission.BODY_TRACKING` is never requested |
| `OVRBody` component | scene | present — nothing requests a tracking session without it |
