"""Run desktop checks in an isolated KWin Wayland session; pass through test flags.

Build Andromeld.slnx from the repository root first. Requires kwin_wayland and dbus-run-session.
For real-phone checks, append --live-game PACKAGE or --live-clipboard.
Use --executable PATH to run a published Andromeld.Desktop.Tests harness.
"""

import argparse
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time


def main() -> int:
    project = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__, add_help=False)
    parser.add_argument("--executable", type=Path)
    options, test_args = parser.parse_known_args()
    command = ([str(options.executable.resolve(strict=True))] if options.executable else
               ["dotnet", "run", "--project", "tests/Andromeld.Desktop.Tests", "--no-build", "--"])
    runtime = tempfile.TemporaryDirectory(prefix="andromeld-wayland-", ignore_cleanup_errors=True)
    os.chmod(runtime.name, 0o700)
    env = os.environ.copy()
    env.pop("DISPLAY", None)
    env.pop("WAYLAND_DISPLAY", None)
    env.update(XDG_RUNTIME_DIR=runtime.name, LIBGL_ALWAYS_SOFTWARE="1", QT_QPA_PLATFORM="offscreen")
    socket_name = "andromeld-check"
    compositor_log = Path(tempfile.gettempdir()) / "andromeld-wayland-compositor.log"
    test_log = Path(tempfile.gettempdir()) / "andromeld-wayland-ui.log"
    with compositor_log.open("w") as log:
        compositor = subprocess.Popen(
            ["dbus-run-session", "--", "kwin_wayland", "--virtual", "--no-global-shortcuts",
             "--no-lockscreen", "--no-kactivities", "--socket", socket_name,
             "--width", "1600", "--height", "1200"],
            env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True,
        )
        try:
            for _ in range(150):
                if Path(runtime.name, socket_name).exists():
                    break
                if compositor.poll() is not None:
                    raise RuntimeError(f"Compositor exited; see {compositor_log}")
                time.sleep(0.1)
            else:
                raise RuntimeError(f"No Wayland socket; see {compositor_log}")
            env["WAYLAND_DISPLAY"] = socket_name
            with test_log.open("w") as output:
                result = subprocess.run(
                    [*command, "--native", *test_args],
                    cwd=project, env=env, stdout=output, stderr=subprocess.STDOUT, timeout=65,
                )
            print(test_log.read_text(), end="")
            print(f"Isolated Wayland test exit: {result.returncode}")
            return result.returncode
        finally:
            try:
                os.killpg(compositor.pid, signal.SIGTERM)
                compositor.wait(timeout=10)
            except ProcessLookupError:
                pass
            except subprocess.TimeoutExpired:
                os.killpg(compositor.pid, signal.SIGKILL)
                compositor.wait()
            # Portal services can leave a disconnected FUSE mount while the private bus exits.
            try:
                runtime.cleanup()
            except OSError:
                pass


if __name__ == "__main__":
    raise SystemExit(main())
