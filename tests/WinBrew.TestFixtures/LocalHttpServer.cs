using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WinBrew.TestFixtures;

/// <summary>
/// In-process HTTP test server on 127.0.0.1 (A16). Serves a directory and
/// lets tests override individual paths to simulate redirects, failures,
/// truncation, lying Content-Length, slow responses and dropped
/// connections. Every request is recorded (path + headers) so tests can
/// assert what the client sent — and what it never requested.
/// Implementation note: <see cref="HttpListener"/> instead of Kestrel keeps
/// the fixture free of an ASP.NET Core framework reference; the behavior
/// under test is the client's, not the server's.
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    public sealed record RecordedRequest(string Path, string? Authorization);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _handlers = new(StringComparer.Ordinal);
    private readonly Task _loop;

    public LocalHttpServer(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory;
        Port = FreePort();
        BaseUrl = new Uri($"http://127.0.0.1:{Port}/");
        _listener.Prefixes.Add(BaseUrl.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string? RootDirectory { get; set; }

    public int Port { get; }

    public Uri BaseUrl { get; }

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public IReadOnlyList<string> RequestedPaths => Requests.Select(r => r.Path).ToList();

    /// <summary>Overrides one path ("/artifacts/x.zip").</summary>
    public void Handle(string path, Func<HttpListenerContext, Task> handler) => _handlers[path] = handler;

    public void Reset(string path) => _handlers.TryRemove(path, out _);

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        var path = context.Request.Url!.AbsolutePath;
        Requests.Enqueue(new RecordedRequest(path, context.Request.Headers["Authorization"]));
        try
        {
            if (_handlers.TryGetValue(path, out var handler))
            {
                await handler(context);
                return;
            }

            var file = RootDirectory is null ? null : Path.Combine(RootDirectory, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (file is null || !File.Exists(file))
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            await WriteAsync(context, await File.ReadAllBytesAsync(file));
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
            // shutting down
        }
        catch (HttpListenerException)
        {
            // client went away (expected in interruption tests)
        }
        catch (IOException)
        {
            // client went away
        }
        catch (ObjectDisposedException)
        {
            // response already aborted by a handler
        }
    }

    // ---- canned behaviors ----------------------------------------------------------------

    public static async Task WriteAsync(HttpListenerContext context, byte[] body, int status = 200)
    {
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = body.LongLength;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    /// <summary>Chunked response without Content-Length — the client must enforce limits on streamed bytes.</summary>
    public static async Task WriteChunkedAsync(HttpListenerContext context, byte[] body)
    {
        context.Response.StatusCode = 200;
        context.Response.SendChunked = true;
        await context.Response.OutputStream.WriteAsync(body);
        context.Response.Close();
    }

    public static Task RedirectAsync(HttpListenerContext context, string location, int status = 302)
    {
        context.Response.StatusCode = status;
        context.Response.RedirectLocation = location;
        context.Response.Close();
        return Task.CompletedTask;
    }

    public static Task StatusAsync(HttpListenerContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.Close();
        return Task.CompletedTask;
    }

    /// <summary>Declares the full length, sends only the first <paramref name="bytes"/> bytes, then aborts the connection.</summary>
    public static async Task DropAfterAsync(HttpListenerContext context, byte[] body, int bytes)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentLength64 = body.LongLength;
        await context.Response.OutputStream.WriteAsync(body.AsMemory(0, Math.Min(bytes, body.Length)));
        await context.Response.OutputStream.FlushAsync();
        context.Response.Abort();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
        {
            // The managed (non-Windows) listener can throw while closing; the port is released anyway.
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
