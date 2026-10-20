"""Launch the same .NET HTTP host used by the planner launchers, with no Unity or Python server."""
import importlib.util
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time

HERE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('native_fixture_editor_server', HERE / 'server.py')
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class NativeHost:
    def __init__(self):
        self.folder = None
        self.process = None
        self.log = None
        if os.environ.get('EDITOR_URL'):
            self.base = os.environ['EDITOR_URL'].rstrip('/')
            return
        self.folder = tempfile.TemporaryDirectory(prefix='battle-toolkit-native-')
        self.process = None
        self.log = None
        try:
            self.bridge = server.build(Path(self.folder.name))
            self.log_path = Path(self.folder.name) / 'host.log'
            self.log = self.log_path.open('w', encoding='utf-8')
            self.process = subprocess.Popen(
                [server.dotnet_runtime(), str(self.bridge), '--root', str(server.ROOT), '--port', '0'],
                stdout=self.log, stderr=subprocess.STDOUT, cwd=server.ROOT)
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                content = self.log_path.read_text(encoding='utf-8')
                match = re.search(r'http://127\.0\.0\.1:\d+/', content)
                if match:
                    self.base = match.group(0).rstrip('/')
                    return
                if self.process.poll() is not None:
                    raise RuntimeError('Native host exited: ' + content)
                time.sleep(0.1)
            raise RuntimeError('Native host did not start: ' + self.log_path.read_text(encoding='utf-8'))
        except Exception:
            self.close()
            raise

    def close(self):
        if self.process is not None and self.process.poll() is None:
            if os.name == 'nt':
                # Terminate compiler children too, so Windows can release their temporary DLLs.
                subprocess.run(['taskkill', '/PID', str(self.process.pid), '/T', '/F'],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                self.process.terminate()
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=10)
        if self.log is not None:
            self.log.close()
        if self.folder is not None:
            self.folder.cleanup()
