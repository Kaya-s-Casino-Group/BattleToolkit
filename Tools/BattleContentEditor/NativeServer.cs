using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Standalone HTTP and process host. Does not reference Unity and never implements combat rules.
internal static class NativeServer
{
    private const string Product = "battle-toolkit-editor";
    private const string Policy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    private const int MaxBody = 4 * 1024 * 1024;
    private const int MaxPlaytestBody = 8 * 1024 * 1024;
    private const int MaxAssetBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions WorkerJson = new JsonSerializerOptions
    { MaxDepth = 100, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Dictionary<string, (string File, string Mime)> Files = new Dictionary<string, (string, string)>
    {
        ["/"] = ("index.html", "text/html"), ["/app.js"] = ("app.js", "text/javascript"),
        ["/schema.js"] = ("schema.js", "text/javascript"), ["/model.js"] = ("model.js", "text/javascript"),
        ["/playtest.js"] = ("playtest.js", "text/javascript"), ["/playtest.css"] = ("playtest.css", "text/css"),
        ["/style.css"] = ("style.css", "text/css")
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--metadata") return CompilerBridge.Run(args);
        if (args.Length == 1 && args[0] == "--validate") return CompilerBridge.Run(Array.Empty<string>());
        if (args.Length == 1 && args[0] == "--playtest") return PlaytestBridge.Run();
        try
        {
            string root = null;
            int port = 18765;
            bool open = false, check = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--root": root = args[++i]; break;
                    case "--port": port = int.Parse(args[++i]); break;
                    case "--open": open = true; break;
                    case "--no-browser": open = false; break;
                    case "--check": check = true; break;
                    default: throw new ArgumentException("未知参数：" + args[i]);
                }
            }
            if (port < 0 || port > 65535) throw new ArgumentException("端口必须在 0 到 65535 之间。");
            root = ResolveRoot(root);
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
            using (var compiler = new RuleProcesses())
            {
                string metadataJson = await compiler.Compile(null);
                using var metadata = JsonDocument.Parse(metadataJson);
                if (check)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { success = true, application = Product,
                        runtime = Environment.Version.ToString(), root, contract = metadata.RootElement.GetProperty("contract").GetString() }));
                    return 0;
                }
                string url = "http://127.0.0.1:" + port;
                if (port != 0 && await IsExistingEditor(url, identity))
                {
                    Console.WriteLine("配置工具已启动：" + url + "/");
                    if (open) OpenBrowser(url);
                    return 0;
                }
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>(), ContentRootPath = root });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.AddServerHeader = false;
                    options.Limits.MaxRequestBodySize = MaxPlaytestBody;
                    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                    options.Listen(IPAddress.Loopback, port);
                });
                await using var app = builder.Build();
                string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                string host = "127.0.0.1:" + port;
                string origin = "http://" + host;
                app.Run(context => Handle(context, compiler, root, host, origin, token, identity, metadata.RootElement));
                try { await app.StartAsync(); }
                catch (IOException)
                {
                    // A simultaneous launcher may have won the bind. Only reuse this tool and checkout.
                    if (port != 0 && await IsExistingEditor(url, identity))
                    {
                        Console.WriteLine("配置工具已启动：" + url + "/");
                        if (open) OpenBrowser(url);
                        return 0;
                    }
                    Console.Error.WriteLine("端口 " + port + " 已被其他程序占用。请关闭旧服务或使用 --port 指定其他端口。");
                    return 2;
                }
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
                url = addresses.Addresses.Single();
                host = new Uri(url).Authority;
                origin = url;
                Console.WriteLine("配置工具：" + url + "/");
                Console.WriteLine("仅本机访问；Ctrl+C 退出。配置导出到下载目录，草稿保存在浏览器中。");
                if (open) OpenBrowser(url);
                await app.WaitForShutdownAsync();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("工具启动失败：" + error.Message);
            return 1;
        }
    }

    private static string ResolveRoot(string root)
    {
        if (root == null)
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent)
                if (Directory.Exists(Path.Combine(current.FullName, "Engine", "Runtime"))) { root = current.FullName; break; }
        }
        if (root == null) throw new ArgumentException("找不到工具目录，请用 --root 指定 BattleToolkit 仓库。");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!File.Exists(Path.Combine(root, "Tools", "BattleContentEditor", "web", "index.html")) ||
            !File.Exists(Path.Combine(root, "Engine", "Runtime", "Composition", "Content", "Examples", "battle-content.xml")))
            throw new ArgumentException("工具目录不完整，请拉取完整的 BattleToolkit 仓库。");
        return root;
    }

    private static async Task<bool> IsExistingEditor(string url, string identity)
    {
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync(url + "/api/health", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 4096) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), timeout.Token)) > 0)
            {
                if (buffer.Length + count > 4096) return false;
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            return json.RootElement.GetProperty("application").GetString() == Product &&
                json.RootElement.GetProperty("workspace").GetString() == identity;
        }
        catch (Exception error) when (error is HttpRequestException || error is OperationCanceledException || error is JsonException || error is KeyNotFoundException || error is InvalidOperationException)
        { return false; }
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url + "/") { UseShellExecute = true }); }
        catch (Exception) { Console.WriteLine("浏览器未能自动打开，请复制上面的地址。"); }
    }

    private static async Task Handle(HttpContext context, RuleProcesses compiler, string root, string host, string origin,
        string token, string identity, JsonElement metadata)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = Policy;
        if (context.Request.Host.Value != host)
        { await Reply(context, 403, new { error = "请使用启动器给出的 127.0.0.1 地址。" }); return; }
        string path = context.Request.Path.Value;
        try
        {
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (path == "/api/health") { await Reply(context, 200, new { application = Product, workspace = identity, protocol = 1 }); return; }
                if (path == "/api/bootstrap") { await Reply(context, 200, new { token, metadata }); return; }
                if (path == "/api/example")
                {
                    await FileReply(context, Path.Combine(root, "Engine", "Runtime", "Composition", "Content", "Examples", "battle-content.xml"), "application/xml");
                    return;
                }
                if (path == "/api/standard")
                {
                    await FileReply(context, Path.Combine(root, "Engine", "Content", "StandardSingleBattle.xml"), "application/xml");
                    return;
                }
                if (Files.TryGetValue(path, out var file))
                { await FileReply(context, Path.Combine(root, "Tools", "BattleContentEditor", "web", file.File), file.Mime); return; }
                if (TryAsset(root, path, out string asset, out string assetMime))
                { await FileReply(context, asset, assetMime); return; }
                await Reply(context, 404, new { error = "未找到页面。" }); return;
            }
            if (!HttpMethods.IsPost(context.Request.Method))
            { await Reply(context, 405, new { error = "请求方法无效。" }); return; }
            string requestOrigin = context.Request.Headers["Origin"].ToString();
            string requestToken = context.Request.Headers["X-Editor-Token"].ToString();
            if ((requestOrigin.Length > 0 && requestOrigin != origin) ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(requestToken), Encoding.UTF8.GetBytes(token)))
            { await Reply(context, 403, new { error = "请求来源无效，请刷新编辑器页面。" }); return; }
            if (path != "/api/validate" && path != "/api/playtest")
            { await Reply(context, 404, new { error = "未知接口。" }); return; }
            bool playtest = path == "/api/playtest";
            int limit = playtest ? MaxPlaytestBody : MaxBody;
            string mime = playtest ? "application/json" : "application/xml";
            if (!context.Request.ContentLength.HasValue || context.Request.ContentLength <= 0 || context.Request.ContentLength > limit)
            { await Reply(context, 413, new { error = "请求为空或超过传输大小限制。" }); return; }
            if (context.Request.ContentType?.Split(';')[0] != mime)
            { await Reply(context, 415, new { error = "需要 " + mime + "。" }); return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[65536];
            int count;
            while ((count = await context.Request.Body.ReadAsync(chunk.AsMemory(), timeout.Token)) > 0)
            {
                if (buffer.Length + count > limit) { await Reply(context, 413, new { error = "请求超过传输大小限制。" }); return; }
                buffer.Write(chunk, 0, count);
            }
            string body = StrictUtf8.GetString(buffer.ToArray());
            if (playtest)
            {
                using var json = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 100 });
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                { await Reply(context, 400, new { error = "试玩请求必须是 JSON 对象。" }); return; }
                // Normalize line breaks before using the existing line-delimited worker protocol.
                body = JsonSerializer.Serialize(json.RootElement, WorkerJson);
            }
            string result = playtest ? await compiler.Playtest(body) : await compiler.Compile(body);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(result, context.RequestAborted);
        }
        catch (Exception error) when (error is JsonException || error is DecoderFallbackException || error is BadHttpRequestException)
        { await Reply(context, 400, new { error = "请求长度、JSON 或 UTF-8 编码无效。" }); }
        catch (Exception error) when (error is InvalidOperationException || error is IOException || error is OperationCanceledException)
        {
            if (!context.RequestAborted.IsCancellationRequested)
                await Reply(context, 503, new { error = "本机规则服务不可用。" + (error is InvalidOperationException ? error.Message : "请重新开局或重启工具。") });
        }
    }

    private static async Task Reply(HttpContext context, int status, object response)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(response), context.RequestAborted);
    }

    private static async Task FileReply(HttpContext context, string file, string mime)
    {
        byte[] bytes = await File.ReadAllBytesAsync(file, context.RequestAborted);
        context.Response.ContentType = mime.StartsWith("image/", StringComparison.Ordinal) && mime != "image/svg+xml" ? mime : mime + "; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes.AsMemory(), context.RequestAborted);
    }

    private static bool TryAsset(string root, string path, out string asset, out string mime)
    {
        asset = null; mime = null;
        if (!path.StartsWith("/assets/", StringComparison.Ordinal)) return false;
        string name = path.Substring("/assets/".Length);
        // Only small top-level presentation images: no nested paths, hidden files, or links outside the tool.
        if (name.Length == 0 || name.Length > 128 || name[0] == '.' ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '.' || character == '-' || character == '_'))) return false;
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".svg": mime = "image/svg+xml"; break;
            case ".png": mime = "image/png"; break;
            case ".webp": mime = "image/webp"; break;
            case ".jpg": case ".jpeg": mime = "image/jpeg"; break;
            default: return false;
        }
        string folder = Path.Combine(root, "Tools", "BattleContentEditor", "web", "assets");
        asset = Path.Combine(folder, name);
        return Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 &&
            File.Exists(asset) && (File.GetAttributes(asset) & FileAttributes.ReparsePoint) == 0 && new FileInfo(asset).Length <= MaxAssetBytes;
    }

    // Rules execute in separate processes so a pathological configuration cannot pin the web host.
    // Validation is bounded to two workers; playtest state lives in one serialized worker.
    private sealed class RuleProcesses : IDisposable
    {
        private const int MaxOutput = 16 * 1024 * 1024;
        private readonly SemaphoreSlim validationSlots = new SemaphoreSlim(2, 2);
        private readonly SemaphoreSlim playtestSlot = new SemaphoreSlim(1, 1);
        private Process playtest;
        private bool disposed;

        internal async Task<string> Compile(string xml)
        {
            if (!await validationSlots.WaitAsync(TimeSpan.FromSeconds(1))) throw new InvalidOperationException("校验器忙碌，请稍后重试。");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                using var process = Start(xml == null ? "--metadata" : "--validate");
                try
                {
                    Task<string> output = ReadResult(process.StandardOutput, false, timeout.Token);
                    Task errors = DrainErrors(process.StandardError, timeout.Token);
                    if (xml != null) await process.StandardInput.WriteAsync(xml.AsMemory(), timeout.Token);
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(timeout.Token);
                    string result = await output;
                    await errors;
                    if (process.ExitCode != 0) throw new InvalidOperationException("C# 校验器执行失败。");
                    using var json = JsonDocument.Parse(result);
                    return result;
                }
                finally { Kill(process); }
            }
            catch (OperationCanceledException) { throw new InvalidOperationException("校验超时，请减少配置中的复杂度后重试。"); }
            finally { validationSlots.Release(); }
        }

        internal async Task<string> Playtest(string request)
        {
            if (!await playtestSlot.WaitAsync(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("试玩正在结算，请稍后再试。");
            try
            {
                if (disposed) throw new InvalidOperationException("试玩服务已停止。");
                if (playtest == null || playtest.HasExited)
                {
                    StopPlaytest();
                    playtest = Start("--playtest");
                    _ = DrainErrors(playtest.StandardError, CancellationToken.None);
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await playtest.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
                await playtest.StandardInput.FlushAsync(timeout.Token);
                string result = await ReadResult(playtest.StandardOutput, true, timeout.Token);
                using var json = JsonDocument.Parse(result);
                return result;
            }
            catch (OperationCanceledException)
            { StopPlaytest(); throw new InvalidOperationException("试玩结算超时，试玩服务已重置，请重新开局。"); }
            catch { StopPlaytest(); throw; }
            finally { playtestSlot.Release(); }
        }

        private static Process Start(string mode)
        {
            string executable = Environment.ProcessPath;
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            info.ArgumentList.Add(mode);
            return Process.Start(info) ?? throw new InvalidOperationException("无法启动 C# 规则进程。");
        }

        private static async Task<string> ReadResult(StreamReader reader, bool line, CancellationToken token)
        {
            char[] chunk = new char[8192];
            var result = new StringBuilder();
            var encoding = Encoding.UTF8.GetEncoder();
            int bytes = 0;
            int count;
            while ((count = await reader.ReadAsync(chunk.AsMemory(), token)) > 0)
            {
                bytes += encoding.GetByteCount(chunk, 0, count, false);
                if (bytes > MaxOutput) throw new InvalidOperationException("规则结果过大，请减少连锁效果后重新开局。");
                result.Append(chunk, 0, count);
                if (line && chunk[count - 1] == '\n') return result.ToString().TrimEnd('\r', '\n');
            }
            if (line) throw new InvalidOperationException("试玩服务已停止，请重新开局。");
            return result.ToString();
        }

        private static async Task DrainErrors(StreamReader reader, CancellationToken token)
        {
            try { char[] chunk = new char[2048]; while (await reader.ReadAsync(chunk.AsMemory(), token) > 0) { } }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is OperationCanceledException) { }
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
        }

        private void StopPlaytest()
        {
            if (playtest == null) return;
            Kill(playtest);
            playtest.Dispose();
            playtest = null;
        }

        public void Dispose() { disposed = true; StopPlaytest(); }
    }
}
