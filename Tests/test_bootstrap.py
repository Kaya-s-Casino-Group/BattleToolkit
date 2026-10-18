"""Launcher contract tests. Only the tests require Python; the tool itself does not.

Run: python3 -m unittest discover -s Tests -p test_bootstrap.py -v
Real Windows/macOS SDK installation and host startup are covered by CI separately.
"""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]


@unittest.skipIf(os.name == "nt", "Bash contract tests run on macOS/Linux; Windows runs its real launcher in CI")
class BootstrapTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="battle bootstrap ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve() / "策划 工具"
        self.root.mkdir()
        shutil.copy(ROOT / "start.command", self.root)
        shutil.copytree(ROOT / "scripts", self.root / "scripts")
        project = self.root / "Build/BattleToolkit.Host/BattleToolkit.Host.csproj"
        project.parent.mkdir(parents=True)
        project.write_text("<Project />", encoding="utf-8")
        self.bin = Path(self.temp.name) / "fake bin"
        self.bin.mkdir()
        self.log = Path(self.temp.name) / "invocations.txt"
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        BATTLE_TOOLKIT_LOCAL_SDK="1", MOCK_LOG=str(self.log))
        self.env.pop("BATTLE_TOOLKIT_DOTNET", None)
        self.dotnet_source = self.bin / "dotnet-sdk10"
        self.write_executable(self.dotnet_source, """#!/bin/bash
if [ "$1" = --list-sdks ]; then printf '10.0.123 [fake sdk]\\n'; exit 0; fi
printf 'CALL\\n' >> "$MOCK_LOG"
printf '%s\\n' "$@" >> "$MOCK_LOG"
if [ "$1" = build ]; then exit "${MOCK_BUILD_EXIT:-0}"; fi
exit "${MOCK_HOST_EXIT:-0}"
""")
        self.env["MOCK_DOTNET_SOURCE"] = str(self.dotnet_source)
        self.installer_source = self.bin / "installer"
        self.write_executable(self.installer_source, """#!/bin/bash
printf 'INSTALL\\n' >> "$MOCK_LOG"
printf '%s\\n' "$@" >> "$MOCK_LOG"
if [ "${MOCK_INSTALL_EXIT:-0}" != 0 ]; then exit "$MOCK_INSTALL_EXIT"; fi
while [ "$#" -gt 0 ]; do
    if [ "$1" = --install-dir ]; then shift; install_dest="$1"; fi
    shift
done
mkdir -p "$install_dest"
cp "$MOCK_DOTNET_SOURCE" "$install_dest/dotnet"
""")
        self.env["MOCK_INSTALLER_SOURCE"] = str(self.installer_source)
        self.write_executable(self.bin / "curl", """#!/bin/bash
printf 'DOWNLOAD\\n' >> "$MOCK_LOG"
if [ "${MOCK_DOWNLOAD_EXIT:-0}" != 0 ]; then exit "$MOCK_DOWNLOAD_EXIT"; fi
while [ "$#" -gt 0 ]; do
    if [ "$1" = --output ]; then shift; output_dest="$1"; fi
    shift
done
cp "$MOCK_INSTALLER_SOURCE" "$output_dest"
""")

    @staticmethod
    def write_executable(path, source):
        path.write_text(source, encoding="utf-8")
        path.chmod(0o755)

    def install_fake_sdk(self):
        sdk = self.root / ".runtime/dotnet/dotnet"
        sdk.parent.mkdir(parents=True)
        shutil.copy(self.dotnet_source, sdk)

    def launch(self, *arguments):
        return subprocess.run(["/bin/bash", str(self.root / "start.command"), *arguments],
                              cwd="/", env=self.env, text=True, capture_output=True, timeout=20)

    def log_text(self):
        return self.log.read_text(encoding="utf-8") if self.log.exists() else ""

    def test_first_run_installs_locally_and_second_run_reuses_sdk(self):
        first = self.launch("--install-only")
        self.assertEqual(0, first.returncode, first.stdout + first.stderr)
        self.assertTrue((self.root / ".runtime/dotnet/dotnet").exists())
        self.assertIn("--channel\n10.0\n--quality\nGA", self.log_text())
        self.assertIn("--no-path", self.log_text())
        self.assertNotIn("CALL", self.log_text())
        second = self.launch("--install-only")
        self.assertEqual(0, second.returncode, second.stdout + second.stderr)
        self.assertEqual(1, self.log_text().count("DOWNLOAD"))

    def test_download_failure_can_retry_without_building(self):
        self.env["MOCK_DOWNLOAD_EXIT"] = "28"
        failed = self.launch("--install-only")
        self.assertEqual(28, failed.returncode)
        self.assertIn("下载失败", failed.stderr)
        self.assertNotIn("INSTALL", self.log_text())
        self.env.pop("MOCK_DOWNLOAD_EXIT")
        self.assertEqual(0, self.launch("--install-only").returncode)

    def test_install_failure_preserves_code_without_building(self):
        self.env["MOCK_INSTALL_EXIT"] = "33"
        failed = self.launch("--check")
        self.assertEqual(33, failed.returncode)
        self.assertIn("安装失败", failed.stderr)
        self.assertNotIn("CALL", self.log_text())

    def test_check_forwards_unicode_root_and_does_not_open_browser(self):
        self.install_fake_sdk()
        result = self.launch("--check")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("--root\n" + str(self.root) + "\n--check", self.log_text())
        self.assertNotIn("--open", self.log_text())
        self.assertNotIn("DOWNLOAD", self.log_text())

    def test_default_launch_opens_browser(self):
        self.install_fake_sdk()
        self.assertEqual(0, self.launch().returncode)
        self.assertIn("--open", self.log_text())

    def test_existing_global_sdk10_is_reused(self):
        shutil.copy(self.dotnet_source, self.bin / "dotnet")
        self.env["BATTLE_TOOLKIT_LOCAL_SDK"] = "0"
        self.assertEqual(0, self.launch("--install-only").returncode)
        self.assertEqual("", self.log_text())
        self.assertFalse((self.root / ".runtime/dotnet/dotnet").exists())

    def test_existing_global_sdk9_triggers_local_install(self):
        self.write_executable(self.bin / "dotnet", "#!/bin/bash\nprintf '9.0.202 [old sdk]\\n'\n")
        self.env["BATTLE_TOOLKIT_LOCAL_SDK"] = "0"
        self.assertEqual(0, self.launch("--install-only").returncode)
        self.assertTrue((self.root / ".runtime/dotnet/dotnet").exists())
        self.assertIn("DOWNLOAD", self.log_text())

    def test_no_browser_and_port_forwarding(self):
        self.install_fake_sdk()
        self.assertEqual(0, self.launch("--no-browser", "--port", "18888").returncode)
        self.assertIn("--port\n18888", self.log_text())
        self.assertNotIn("--open", self.log_text())

    def test_build_failure_preserves_code_and_never_starts_host(self):
        self.install_fake_sdk()
        self.env["MOCK_BUILD_EXIT"] = "47"
        result = self.launch("--check")
        self.assertEqual(47, result.returncode)
        self.assertEqual(1, self.log_text().count("CALL"))
        self.assertNotIn("BattleToolkit.Host.dll", self.log_text())

    def test_host_failure_preserves_code(self):
        self.install_fake_sdk()
        self.env["MOCK_HOST_EXIT"] = "48"
        self.assertEqual(48, self.launch("--no-browser").returncode)

    def test_old_explicit_sdk_is_rejected(self):
        old_sdk = self.bin / "dotnet-old"
        self.write_executable(old_sdk, "#!/bin/bash\nprintf '9.0.202 [old sdk]\\n'\n")
        self.env["BATTLE_TOOLKIT_DOTNET"] = str(old_sdk)
        result = self.launch("--check")
        self.assertNotEqual(0, result.returncode)
        self.assertIn(".NET 10", result.stderr)
        self.assertEqual("", self.log_text())

    def test_bad_argument_does_not_download(self):
        self.assertEqual(2, self.launch("--missing-option").returncode)
        self.assertEqual("", self.log_text())


if __name__ == "__main__":
    unittest.main()
