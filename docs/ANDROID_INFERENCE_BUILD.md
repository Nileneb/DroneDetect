# Android ground station

The phone is the ground station: it joins the AR.Drone 2.0's Wi-Fi, decodes the
front-camera video, runs MiDaS, YOLO, OSNet and the trained policy with Sentis,
and sends AT commands back to the drone.

```
AR.Drone 2.0 ── H.264 (TCP 5555) ──► MediaCodec ──► RGBA frame ──► MiDaS (84×84 depth) ─┐
             ── navdata (UDP 5554) ─────────────────────────────────────────────────────┤
                                                    YOLO ─► target tracker (tap + OSNet) ─┤
             ◄── AT*PCMD 30 Hz (UDP 5556) ◄── safety envelope ◄── DroneFollow policy ◄────┘
```

## Build

1. Copy the trained policy: `cp results/<run-id>/DroneFollow.onnx Assets/Models/DroneFollow.onnx`.
2. In Unity: **DroneDetect → Build → Android Ground Station APK** (or batch mode:
   `Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod DroneFollowBuild.BuildAndroidGroundStation`).
   This regenerates `Assets/Scenes/DroneFollowAndroid.unity` and writes
   `Builds/Android/DroneFollowGroundStation.apk` (IL2CPP, ARM64, Vulkan).
3. `adb install -r Builds/Android/DroneFollowGroundStation.apk`.

Do not build on the machine while training runs in its editor: a second Unity
instance froze the whole system once.

## Flying

1. Connect the phone to the drone's `ardrone2_xxxxxx` Wi-Fi, start the app.
   The status line shows navdata and the video frame counter.
2. **Shadow mode is on by default**: the policy runs and its commands are shown,
   but the drone only hovers. Take off, tap yourself in the video to lock the
   target, walk around and check that the displayed commands point the right way.
3. Switch shadow mode off. The drone follows; if the target is lost for 1.5 s it
   hovers, it never climbs above 2.5 m, and it lands on low battery, link loss
   or when the app goes to the background. **EMERGENCY** cuts the motors.

## Testing without the phone

In the Editor (Linux/macOS/Windows with `ffmpeg` on PATH) the same scene runs
against the real drone from a laptop on the drone's Wi-Fi: open
`DroneFollowAndroid.unity` and press Play. EditMode tests cover the AT/navdata/PaVE
wire format, the sim↔drone sign conventions and the target tracker.
