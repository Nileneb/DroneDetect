# Android inference build preparation

`tools/build_android_inference.sh` is intentionally outside `Assets/`. Invoking
it without `--build` only checks the scene path and Unity Android Build Support;
it never launches Unity or causes an editor recompile.

When a separate inference scene is ready:

1. Stop Play mode and the ML-Agents trainer.
2. Configure that scene with exactly one active `DroneFollowAgent`, a trained
   DroneFollow model assigned to its `BehaviorParameters`, `Behavior Type` set
   to `Inference Only`, a matching `DecisionRequester`, and a grayscale
   84x84 depth sensor connected to the camera depth output.
3. Implement and attach the production `IDroneFollowController` and
   `IDroneTargetDetectionSource` adapters. The current detector in the training
   scene is a simulation surrogate; there is no YOLO or real-drone controller
   implementation in this repository yet.
4. Install Android Build Support (SDK, NDK, and OpenJDK) for the project's
   Unity version in Unity Hub.
5. Run a read-only preflight:

   ```bash
   ./tools/build_android_inference.sh \
     --scene Assets/Scenes/DroneFollowAndroid.unity
   ```

6. Only after training is stopped, explicitly request the build:

   ```bash
   ./tools/build_android_inference.sh \
     --scene Assets/Scenes/DroneFollowAndroid.unity \
     --output Builds/DroneFollowAndroid.apk \
     --build
   ```

The build command refuses to run while a Unity editor/build process for this
project or an ML-Agents trainer working in this project is active. It adds a
temporary Unity Editor build entry point only for the batch build and removes
it afterward. The selected scene must already reference its policy and
inference components; the script does not download, convert, or invent YOLO or
depth-estimation models.

The simulation's Unity depth buffer is not a monocular depth-estimation model.
For a real camera feed, provide a compatible depth sensor or integrate a
depth-estimation model and adapt its output to the policy's depth input. The
YOLO output must likewise be adapted to the four-value target-detection
contract used by `DroneFollowAgent`.
