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
3. Implement and attach the production `IDroneController` adapter. The
   training scene uses `DroneFollowSentisPerception` on Main Camera for MiDaS
   depth and YOLO person detection. Configure that component and the same model
   assets in any separate inference scene; setup configures only the active
   DroneFollow training scene.
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
inference components; the script does not download or convert models.

The Sentis package is declared directly at version 2.6.1. The MiDaS output is
adapted to the policy's existing 84x84 grayscale depth input, and YOLO person
boxes to the four-value target-detection contract. The provided YOLO ONNX
metadata identifies its license as AGPL-3.0; verify the model's obligations
before redistributing the project or offering it as a network service.
