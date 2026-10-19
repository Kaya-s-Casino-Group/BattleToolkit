"""Run browser regressions against a temporary instance of the shipping .NET host."""
import os
import subprocess

from native_fixture import HERE, NativeHost, server


def main():
    host = NativeHost()
    try:
        environment = dict(os.environ, EDITOR_URL=host.base)
        for script in ['browser.cjs', 'playtest-browser.cjs', 'h5-scene-browser.cjs']:
            subprocess.run(['node', str(HERE / 'tests' / script)], cwd=server.ROOT, env=environment, check=True)
    finally:
        host.close()


if __name__ == '__main__':
    main()
