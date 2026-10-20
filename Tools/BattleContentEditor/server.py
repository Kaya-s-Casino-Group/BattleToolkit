#!/usr/bin/env python3
"""Python integration-test compatibility host. Designers use the standalone .NET launcher."""
import argparse
import json
import os
import re
from pathlib import Path
import secrets
import queue
import shutil
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent.parent
MAX_BODY = 4 * 1024 * 1024  # UTF-8 transport limit; reader additionally limits characters.
MAX_PLAYTEST_BODY = 8 * 1024 * 1024  # JSON escaping adds overhead to XML.
MAX_ASSET_BYTES = 512 * 1024
ASSET_MIMES = {'.svg': 'image/svg+xml', '.png': 'image/png', '.webp': 'image/webp', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg'}


def presentation_asset(route):
    """Only serve small top-level image files in this tool's assets folder."""
    if not route.startswith('/assets/'):
        return None
    name = route[len('/assets/'):]
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,127}', name):
        return None
    folder = HERE / 'web' / 'assets'
    path = folder / name
    mime = ASSET_MIMES.get(path.suffix.lower())
    if mime and not folder.is_symlink() and path.is_file() and not path.is_symlink() and path.stat().st_size <= MAX_ASSET_BYTES:
        return path, mime
    return None


def dotnet_runtime():
    configured = os.environ.get('POKER_DOTNET')
    local = ROOT / '.runtime/dotnet' / ('dotnet.exe' if os.name == 'nt' else 'dotnet')
    candidate = configured or (str(local) if local.is_file() else shutil.which('dotnet'))
    if not candidate:
        raise RuntimeError('找不到 .NET SDK，请先运行仓库根目录的 start.cmd 或 start.command。')
    return candidate


def build(output):
    output.mkdir(parents=True, exist_ok=True)
    project = ROOT / 'Build/BattleToolkit.Host/BattleToolkit.Host.csproj'
    subprocess.run([dotnet_runtime(), 'build', str(project), '--configuration', 'Release',
                    '--output', str(output), '--nologo', '--verbosity', 'quiet',
                    '-maxcpucount:1', '-nodeReuse:false', '-p:UseSharedCompilation=false'], cwd=ROOT, check=True)
    return output / 'BattleToolkit.Host.dll'


class Compiler:
    def __init__(self, bridge):
        self.bridge = bridge
        self.runtime = dotnet_runtime()
        self.slots = threading.BoundedSemaphore(2)
        self.playtest_lock = threading.Lock()
        self.playtest_process = None
        self.playtest_stderr = None
        self.playtest_output = None

    def call(self, xml=None):
        if not self.slots.acquire(timeout=1):
            raise RuntimeError('校验器忙碌，请稍后重试。')
        try:
            result = subprocess.run([self.runtime, str(self.bridge)] + (['--metadata'] if xml is None else ['--validate']),
                                    input=xml, encoding='utf-8', capture_output=True, timeout=25)
            if result.returncode:
                raise RuntimeError('C# 校验器执行失败：' + result.stderr[:1200])
            return json.loads(result.stdout.lstrip('\ufeff'))
        finally:
            self.slots.release()

    def _stop_playtest(self):
        process = self.playtest_process
        self.playtest_process = None
        self.playtest_output = None
        if process is not None:
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            process.stdin.close()
            process.stdout.close()
        if self.playtest_stderr is not None:
            self.playtest_stderr.close()
            self.playtest_stderr = None

    def close(self):
        with self.playtest_lock:
            self._stop_playtest()

    def playtest(self, request):
        # A single request stream serializes mutations, including across HTTP threads.
        if not self.playtest_lock.acquire(timeout=2):
            raise RuntimeError('试玩正在结算，请稍后再试。')
        try:
            if self.playtest_process is None or self.playtest_process.poll() is not None:
                self._stop_playtest()
                self.playtest_stderr = tempfile.TemporaryFile()
                self.playtest_process = subprocess.Popen(
                    [self.runtime, str(self.bridge), '--playtest'],
                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.playtest_stderr)
                # Windows cannot select() subprocess pipes. One bounded reader queue works on all hosts.
                responses = queue.Queue(maxsize=1)
                self.playtest_output = responses
                threading.Thread(target=self._read_playtest, args=(self.playtest_process.stdout, responses), daemon=True).start()
            process = self.playtest_process
            data = (json.dumps(request, ensure_ascii=False, allow_nan=False) + '\n').encode('utf-8')
            process.stdin.write(data)
            process.stdin.flush()
            try:
                result = self.playtest_output.get(timeout=25)
            except queue.Empty:
                raise RuntimeError('试玩结算超时，试玩服务已重置，请重新开局。')
            if isinstance(result, Exception):
                raise result
            return json.loads(result.decode('utf-8-sig'))
        except (RuntimeError, OSError, ValueError):
            self._stop_playtest()
            raise
        finally:
            self.playtest_lock.release()


    @staticmethod
    def _read_playtest(stream, responses):
        try:
            while True:
                line = stream.readline(16 * 1024 * 1024 + 1)
                if not line:
                    raise RuntimeError('试玩服务已停止，请重新开局。')
                if len(line) > 16 * 1024 * 1024 or not line.endswith(b'\n'):
                    raise RuntimeError('试玩结果过大，请减少配置中的连锁效果后重新开局。')
                responses.put(line)
        except (OSError, ValueError, RuntimeError) as error:
            try:
                responses.put_nowait(error)
            except queue.Full:
                pass


def make_server(compiler, port=18767):
    token = secrets.token_urlsafe(32)
    metadata = compiler.call()
    files = {'/': ('index.html', 'text/html'), '/app.js': ('app.js', 'text/javascript'),
             '/schema.js': ('schema.js', 'text/javascript'), '/model.js': ('model.js', 'text/javascript'),
             '/playtest.js': ('playtest.js', 'text/javascript'), '/playtest.css': ('playtest.css', 'text/css'),
             '/style.css': ('style.css', 'text/css')}

    class Handler(BaseHTTPRequestHandler):
        def reply(self, code, data, content_type='application/json'):
            body = json.dumps(data, ensure_ascii=False).encode('utf-8') if content_type == 'application/json' else data
            self.send_response(code)
            self.send_header('Content-Type', content_type if content_type.startswith('image/') and content_type != 'image/svg+xml' else content_type + '; charset=utf-8')
            self.send_header('Content-Length', str(len(body)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.send_header('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
            self.end_headers()
            self.wfile.write(body)

        def valid_host(self):
            return self.headers.get('Host') == '127.0.0.1:' + str(self.server.server_port)

        def do_GET(self):
            if not self.valid_host():
                self.reply(403, {'error': '请使用启动器给出的 127.0.0.1 地址。'}); return
            route = urlsplit(self.path).path
            if route == '/api/bootstrap':
                self.reply(200, {'token': token, 'metadata': metadata}); return
            if route == '/api/example':
                path = ROOT / 'Engine/Runtime/Composition/Content/Examples/battle-content.xml'
                self.reply(200, path.read_bytes(), 'application/xml'); return
            if route == '/api/standard':
                path = next(iter(sorted((ROOT / 'Engine/Content').glob('StandardSingleBattle-????-??-??.xml'), reverse=True)), ROOT / 'Engine/Content/StandardSingleBattle.xml')
                self.reply(200, path.read_bytes(), 'application/xml'); return
            if route in files:
                filename, mime = files[route]
                self.reply(200, (HERE / 'web' / filename).read_bytes(), mime); return
            asset = presentation_asset(route)
            if asset:
                path, mime = asset
                self.reply(200, path.read_bytes(), mime); return
            self.reply(404, {'error': '未找到页面。'})

        def do_POST(self):
            origin = 'http://127.0.0.1:' + str(self.server.server_port)
            if not self.valid_host() or self.headers.get('Origin', origin) != origin or not secrets.compare_digest(self.headers.get('X-Editor-Token', ''), token):
                self.reply(403, {'error': '请求来源无效，请刷新编辑器页面。'}); return
            if self.path not in ('/api/validate', '/api/playtest'):
                self.reply(404, {'error': '未知接口。'}); return
            try:
                length = int(self.headers.get('Content-Length', '0'))
                playtest = self.path == '/api/playtest'
                limit = MAX_PLAYTEST_BODY if playtest else MAX_BODY
                content_type = 'application/json' if playtest else 'application/xml'
                if not 0 < length <= limit:
                    self.reply(413, {'error': '请求为空或超过传输大小限制。'}); return
                if self.headers.get('Content-Type', '').split(';')[0] != content_type:
                    self.reply(415, {'error': '需要 ' + content_type + '。'}); return
                self.connection.settimeout(30)
                body = self.rfile.read(length).decode('utf-8')
                if playtest:
                    request = json.loads(body)
                    if not isinstance(request, dict):
                        self.reply(400, {'error': '试玩请求必须是 JSON 对象。'}); return
                    self.reply(200, compiler.playtest(request))
                else:
                    self.reply(200, compiler.call(body))
            except (ValueError, UnicodeError):
                self.reply(400, {'error': '请求长度、JSON 或 UTF-8 编码无效。'})
            except (RuntimeError, subprocess.TimeoutExpired, OSError) as error:
                self.reply(503, {'error': '本机规则服务不可用。' + str(error)})

        def log_message(self, fmt, *args):
            # Paths/status only; never log submitted XML.
            pass

    class EditorServer(ThreadingHTTPServer):
        def server_close(self):
            super().server_close()
            compiler.close()

    return EditorServer(('127.0.0.1', port), Handler)


def main():
    parser = argparse.ArgumentParser(description='开发测试兼容入口（Python 3 + .NET SDK）；策划请使用仓库根目录启动器。')
    parser.add_argument('--port', type=int, default=18767)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='battle-content-editor-') as folder:
        print('正在编译现有 C# 规则程序集……', flush=True)
        compiler = Compiler(build(Path(folder)))
        with make_server(compiler, args.port) as server:
            print('配置工具：http://127.0.0.1:%d' % server.server_port, flush=True)
            print('仅本机访问；Ctrl+C 退出。草稿保存在浏览器中，XML 导出到下载目录。', flush=True)
            try:
                server.serve_forever()
            except KeyboardInterrupt:
                print('\n已停止配置工具。')


if __name__ == '__main__':
    main()
