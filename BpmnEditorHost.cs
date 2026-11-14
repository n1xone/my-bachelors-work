using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

internal static class BpmnEditorHost
{
    private const int MaxModelBytes = 10 * 1024 * 1024;
    private const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static TaskCompletionSource? editorCloseRequest;

    public static IDisposable RouteCtrlCToEditor(TaskCompletionSource closeRequest)
    {
        ArgumentNullException.ThrowIfNull(closeRequest);
        if (Interlocked.CompareExchange(ref editorCloseRequest, closeRequest, null) is not null)
            throw new InvalidOperationException("An editor is already handling Ctrl+C.");

        return new EditorInterruptScope(closeRequest);
    }

    public static bool TryHandleEditorCtrlC() =>
        Volatile.Read(ref editorCloseRequest)?.TrySetResult() ?? false;

    public static IReadOnlyList<string> DiscoverModelFiles(string? workspaceRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? Directory.GetCurrentDirectory() : Path.GetFullPath(workspaceRoot);
        var sandboxDirectory = Path.Combine(root, ".sandbox-models");
        if (!Directory.Exists(sandboxDirectory)) return [];
        var files = Directory.EnumerateFiles(sandboxDirectory, "*.bpmn", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(sandboxDirectory, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part.StartsWith(".import-", StringComparison.Ordinal)));

        return Bakalarka.ConsoleApp.SandboxWorkspace.LatestDrafts(files
            .Select(file =>
            {
                var source = Path.Combine(root, Path.GetRelativePath(sandboxDirectory, file));
                if (File.Exists(source))
                    return source;

                var legacySource = Path.Combine(root, "models", Path.GetFileName(file));
                return File.Exists(legacySource) &&
                    File.ReadAllBytes(legacySource).AsSpan().SequenceEqual(File.ReadAllBytes(file))
                    ? legacySource
                    : file;
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public static string RelativePathForDisplay(string path, string workspaceRoot)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), Path.GetFullPath(path));
        return relative.Replace('\\', '/');
    }

    public static string GetSandboxCopyPath(string modelPath, string? workspaceRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? Directory.GetCurrentDirectory() : Path.GetFullPath(workspaceRoot);
        var sandboxDirectory = Path.Combine(root, ".sandbox-models");
        var fullPath = Path.GetFullPath(modelPath);

        if (IsWithin(sandboxDirectory, fullPath))
            return fullPath;

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (!IsRelativeInsideWorkspace(relativePath))
            relativePath = Path.GetFileName(fullPath);

        return Path.GetFullPath(Path.Combine(sandboxDirectory, relativePath));
    }

    public static string ResolveEditableModelPath(string relativePath, string workspaceRoot)
    {
        var sourcePath = ResolveProjectPath(relativePath, workspaceRoot);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("BPMN model was not found.", sourcePath);

        var sandboxPath = GetSandboxCopyPath(sourcePath, workspaceRoot);
        if (!string.Equals(sourcePath, sandboxPath, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sandboxPath)!);
            if (!File.Exists(sandboxPath))
                File.Copy(sourcePath, sandboxPath);
        }

        return sandboxPath;
    }

    public static string ResolveProjectPath(string? relativeOrAbsolutePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            throw new ArgumentException("Path is required.");

        var root = Path.GetFullPath(workspaceRoot);
        var fullPath = Path.IsPathRooted(relativeOrAbsolutePath)
            ? Path.GetFullPath(relativeOrAbsolutePath)
            : Path.GetFullPath(Path.Combine(root, relativeOrAbsolutePath));

        if (!IsRelativeInsideWorkspace(Path.GetRelativePath(root, fullPath)))
            throw new InvalidOperationException("Selected BPMN file must stay inside the project workspace.");
        if (!string.Equals(Path.GetExtension(fullPath), ".bpmn", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selected file must have a .bpmn extension.");

        return fullPath;
    }

    public static async Task<string?> RunAsync(string modelPath, string webRoot, CancellationToken ct, Func<string, CancellationToken, Task>? onSaved = null, string? engineAddress = null)
    {
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
            throw new DirectoryNotFoundException("The BPMN editor is not built. Run: cd BpmnEditor && npm ci && npm run build");

        var sourcePath = Path.GetFullPath(modelPath);
        var sandboxPath = GetSandboxCopyPath(sourcePath, Directory.GetCurrentDirectory());
        Directory.CreateDirectory(Path.GetDirectoryName(sandboxPath)!);
        if (!File.Exists(sandboxPath))
            File.Copy(sourcePath, sandboxPath);
        var workspaceRoot = Directory.GetCurrentDirectory();
        var initialModelPath = RelativePathForDisplay(
            IsWithin(workspaceRoot, sourcePath) ? sourcePath : sandboxPath,
            workspaceRoot);
        if (IsWithin(Path.Combine(workspaceRoot, ".sandbox-models"), sourcePath))
        {
            var originalPath = Path.Combine(workspaceRoot, Path.GetRelativePath(Path.Combine(workspaceRoot, ".sandbox-models"), sourcePath));
            if (File.Exists(originalPath))
                initialModelPath = RelativePathForDisplay(originalPath, workspaceRoot);
        }
        var activeModelPath = initialModelPath;

        using var listener = new HttpListener();
        var closeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var editorInterruptScope = RouteCtrlCToEditor(closeRequested);
        var address = $"http://127.0.0.1:{GetFreePort()}/";
        listener.Prefixes.Add(address);
        listener.Start();

        Console.WriteLine($"BPMN editor URL (open this in a browser): {address}");
        Console.WriteLine($"Sandbox copy: {sandboxPath}");
        Console.WriteLine("Edits are saved to the sandbox copy only. The source BPMN file is unchanged.");
        Console.WriteLine(onSaved is null ? "Save your edits, then press Ctrl+C to return." : "Save deploys and updates active instances on the selected engine. Complete requested values in this terminal; Ctrl+C returns to the menu.");

        try
        {
            if (OperatingSystem.IsLinux())
            {
                using var browser = Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ArgumentList = { address }
                });
                if (browser is null)
                    Console.WriteLine("Browser could not be started automatically; open the editor URL above.");
            }
            else
                Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"Browser could not be started automatically ({ex.Message}). Open the editor URL above.");
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var contextTask = listener.GetContextAsync();
                var completed = await Task.WhenAny(contextTask, closeRequested.Task.WaitAsync(ct));
                if (completed != contextTask)
                    break;

                var context = await contextTask.WaitAsync(ct);
                await HandleRequestAsync(context, sandboxPath, initialModelPath, webRoot,
                    selectedPath => activeModelPath = selectedPath, ct, onSaved, engineAddress);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        return ct.IsCancellationRequested ? null : activeModelPath;
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext context,
        string modelPath,
        string displayFileName,
        string webRoot,
        Action<string> modelSelected,
        CancellationToken ct, Func<string, CancellationToken, Task>? onSaved, string? engineAddress)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/api/models" && context.Request.HttpMethod == "GET")
            {
                var workspaceRoot = Directory.GetCurrentDirectory();
                var files = DiscoverModelFiles(workspaceRoot)
                    .Select(file => new
                    {
                        label = Bakalarka.ConsoleApp.SandboxWorkspace.Label(file),
                        value = RelativePathForDisplay(file, workspaceRoot)
                    })
                    .ToArray();
                await WriteAsync(context.Response, 200, "application/json; charset=utf-8",
                    JsonSerializer.SerializeToUtf8Bytes(files), ct);
                return;
            }

            if (path == "/api/model")
            {
                var workspaceRoot = Directory.GetCurrentDirectory();
                var requestedPath = context.Request.QueryString["path"];
                if (context.Request.HttpMethod == "GET")
                {
                    var targetPath = string.IsNullOrWhiteSpace(requestedPath)
                        ? modelPath
                        : ResolveEditableModelPath(requestedPath, workspaceRoot);
                    modelSelected(string.IsNullOrWhiteSpace(requestedPath) ? displayFileName : requestedPath);
                    var xml = await File.ReadAllTextAsync(targetPath, ct);
                    await WriteAsync(context.Response, 200, "application/json; charset=utf-8",
                        JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            engineAddress,
                            fileName = Path.GetFileName(targetPath),
                            relativePath = string.IsNullOrWhiteSpace(requestedPath) ? displayFileName : requestedPath,
                            xml
                        }), ct);
                    return;
                }

                if (context.Request.HttpMethod == "PUT")
                {
                    var targetPath = string.IsNullOrWhiteSpace(requestedPath)
                        ? modelPath
                        : ResolveEditableModelPath(requestedPath, workspaceRoot);
                    modelSelected(string.IsNullOrWhiteSpace(requestedPath) ? displayFileName : requestedPath);
                    var xml = await ReadBodyAsync(context.Request, ct);
                    var document = XDocument.Parse(xml, LoadOptions.None);
                    if (document.Root?.Name.LocalName != "definitions" || document.Root.Name.NamespaceName != BpmnNamespace)
                        throw new InvalidDataException("Expected BPMN XML with a definitions root element.");

                    var temporaryPath = targetPath + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temporaryPath, xml, new UTF8Encoding(false), ct);
                        File.Move(temporaryPath, targetPath, true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    }

                    if (onSaved is not null) await onSaved(targetPath, ct);
                    await WriteTextAsync(context.Response, 200, onSaved is null ? "Draft saved" : "Saved; active instances updated on " + engineAddress, ct);
                    return;
                }

                context.Response.Headers["Allow"] = "GET, PUT";
                await WriteTextAsync(context.Response, 405, "Method not allowed", ct);
                return;
            }

            if (context.Request.HttpMethod != "GET")
            {
                await WriteTextAsync(context.Response, 405, "Method not allowed", ct);
                return;
            }

            var relativePath = Uri.UnescapeDataString(path.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relativePath)) relativePath = "index.html";
            var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
            var rootPrefix = Path.GetFullPath(webRoot) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                await WriteTextAsync(context.Response, 404, "Not found", ct);
                return;
            }

            await WriteAsync(context.Response, 200, ContentType(fullPath), await File.ReadAllBytesAsync(fullPath, ct), ct);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine("Engine update failed: " + ex.Message);
            await WriteTextAsync(context.Response, 409, "Draft saved; engine update failed: " + ex.Message, ct);
        }
        catch (Bakalarka.ConsoleApp.BackException)
        {
            await WriteTextAsync(context.Response, 409, "Draft saved; update canceled. Prepared instance remains suspended.", ct);
        }
        catch (InvalidDataException ex)
        {
            await WriteTextAsync(context.Response, 400, ex.Message, ct);
        }
        catch (System.Xml.XmlException ex)
        {
            await WriteTextAsync(context.Response, 400, ex.Message, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await WriteTextAsync(context.Response, 400, ex.Message, ct);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (request.ContentLength64 > MaxModelBytes)
            throw new InvalidDataException("BPMN file is too large (10 MB maximum).");

        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > MaxModelBytes)
                throw new InvalidDataException("BPMN file is too large (10 MB maximum).");
            await body.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static Task WriteTextAsync(HttpListenerResponse response, int statusCode, string text, CancellationToken ct) =>
        WriteAsync(response, statusCode, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), ct);

    private static async Task WriteAsync(HttpListenerResponse response, int statusCode, string contentType, byte[] body, CancellationToken ct)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        response.Headers["X-Content-Type-Options"] = "nosniff";
        await response.OutputStream.WriteAsync(body, ct);
    }

    private static bool IsWithin(string directory, string path) =>
        IsRelativeInsideWorkspace(Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path)));

    private static bool IsRelativeInsideWorkspace(string relativePath) =>
        !Path.IsPathRooted(relativePath) &&
        relativePath != ".." &&
        !relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
        !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private sealed class EditorInterruptScope(TaskCompletionSource closeRequest) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref editorCloseRequest, null, closeRequest);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}