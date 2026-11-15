import logging

from packaging.version import InvalidVersion, Version

from mlagents.trainers.learn import main
from mlagents_envs.environment import UnityEnvironment


logger = logging.getLogger("mlagents_envs.environment")


def check_communication_compatibility(
    unity_com_ver: str, python_api_version: str, unity_package_version: str
) -> bool:
    try:
        unity_version = Version(str(unity_com_ver))
        python_version = Version(str(python_api_version))
    except InvalidVersion:
        return False

    if unity_version.major == 0:
        if (unity_version.major, unity_version.minor) != (
            python_version.major,
            python_version.minor,
        ):
            return False
    elif unity_version.major != python_version.major:
        return False

    logger.info(
        "Connected to Unity environment with package version %s "
        "and communication version %s",
        unity_package_version,
        unity_com_ver,
    )
    return True


def use_legacy_onnx_export() -> None:
    # WHY: torch >= 2.9 exports ONNX through the dynamo exporter, which needs `onnxscript`. mlagents pins
    # onnx 1.15 / protobuf < 3.21, which cannot coexist with it. The TorchScript exporter needs neither.
    import functools

    import torch

    original_export = torch.onnx.export

    @functools.wraps(original_export)
    def export(*args, **kwargs):
        kwargs.setdefault("dynamo", False)
        return original_export(*args, **kwargs)

    torch.onnx.export = export


def main_with_compatible_version_parser() -> None:
    UnityEnvironment._check_communication_compatibility = staticmethod(
        check_communication_compatibility
    )
    use_legacy_onnx_export()
    main()


if __name__ == "__main__":
    main_with_compatible_version_parser()
