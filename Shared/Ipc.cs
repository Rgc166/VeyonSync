using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VeyonSync.Shared;

public sealed class IpcRequest
{
    public string Cmd { get; set; } = "";
    public JsonObject? Args { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonNode? Data { get; set; }
}

public sealed class IpcException(string message) : Exception(message);

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static JsonNode? ToNode(object? o) => o == null ? null : JsonSerializer.SerializeToNode(o, o.GetType(), Options);
    public static T? From<T>(JsonNode? n) => n == null ? default : n.Deserialize<T>(Options);

    public static string Str(this JsonObject? a, string key, string def = "") =>
        a != null && a.TryGetPropertyValue(key, out var v) && v != null ? v.GetValue<string>() : def;
    public static bool Bool(this JsonObject? a, string key, bool def = false) =>
        a != null && a.TryGetPropertyValue(key, out var v) && v != null ? v.GetValue<bool>() : def;
    public static long Long(this JsonObject? a, string key, long def = 0) =>
        a != null && a.TryGetPropertyValue(key, out var v) && v != null ? v.GetValue<long>() : def;
}

/// <summary>
/// IPC local por Named Pipe. ACL: somente Administradores e SYSTEM. O servidor também confere,
/// personificando o cliente, se o token está elevado como administrador (a interface não é a única barreira).
/// Mensagens: inteiro de 4 bytes (tamanho) + JSON UTF-8.
/// </summary>
public static class Ipc
{
    const int MaxMessage = 32 * 1024 * 1024;

    static async Task WriteAsync(Stream s, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxMessage) throw new IpcException("Mensagem grande demais.");
        await s.WriteAsync(BitConverter.GetBytes(bytes.Length), ct);
        await s.WriteAsync(bytes, ct);
        await s.FlushAsync(ct);
    }

    static async Task<string?> ReadAsync(Stream s, CancellationToken ct)
    {
        var len = new byte[4];
        int got = 0;
        while (got < 4)
        {
            var n = await s.ReadAsync(len.AsMemory(got, 4 - got), ct);
            if (n == 0) return null;
            got += n;
        }
        var size = BitConverter.ToInt32(len);
        if (size < 0 || size > MaxMessage) throw new IpcException("Tamanho de mensagem inválido.");
        var buf = new byte[size];
        got = 0;
        while (got < size)
        {
            var n = await s.ReadAsync(buf.AsMemory(got, size - got), ct);
            if (n == 0) return null;
            got += n;
        }
        return Encoding.UTF8.GetString(buf);
    }

    public static async Task RunServerAsync(string pipeName, Func<IpcRequest, CancellationToken, Task<object?>> handler,
        Action<string> log, CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 65536, 65536, security);
            }
            catch (Exception e)
            {
                log("Falha ao criar pipe: " + e.Message);
                await Task.Delay(2000, ct);
                continue;
            }
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            _ = Task.Run(() => ServeAsync(pipe, handler, log, ct), ct);
        }
    }

    static async Task ServeAsync(NamedPipeServerStream pipe, Func<IpcRequest, CancellationToken, Task<object?>> handler,
        Action<string> log, CancellationToken ct)
    {
        using (pipe)
        {
            bool admin = false;
            try
            {
                pipe.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent();
                    admin = id.IsSystem || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                });
            }
            catch (Exception e) { log("Não foi possível identificar o cliente do pipe: " + e.Message); }

            try
            {
                while (pipe.IsConnected && !ct.IsCancellationRequested)
                {
                    var text = await ReadAsync(pipe, ct);
                    if (text == null) break;
                    IpcResponse resp;
                    if (!admin)
                    {
                        resp = new IpcResponse { Ok = false, Error = "Acesso negado: abra o aplicativo como administrador." };
                    }
                    else
                    {
                        try
                        {
                            var req = JsonSerializer.Deserialize<IpcRequest>(text, IpcJson.Options)
                                      ?? throw new IpcException("Pedido vazio.");
                            var result = await handler(req, ct);
                            resp = new IpcResponse { Ok = true, Data = IpcJson.ToNode(result) };
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            resp = new IpcResponse { Ok = false, Error = e is IpcException or InvalidDataException or InvalidOperationException ? e.Message : e.GetType().Name + ": " + e.Message };
                            if (e is not IpcException && e is not InvalidDataException) log("Erro em comando administrativo: " + e);
                        }
                    }
                    await WriteAsync(pipe, JsonSerializer.Serialize(resp, IpcJson.Options), ct);
                }
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>Cliente usado pelas interfaces. Uma conexão por chamada: simples e sem estado compartilhado.</summary>
    public sealed class Client(string pipeName)
    {
        public async Task<JsonNode?> CallAsync(string cmd, object? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Impersonation);
            try { await pipe.ConnectAsync(3000, cts.Token); }
            catch (TimeoutException) { throw new IpcException("Serviço indisponível: o serviço do Veyon Sync não está em execução."); }
            catch (UnauthorizedAccessException) { throw new IpcException("Acesso negado ao serviço: abra o aplicativo como administrador."); }
            var req = new IpcRequest { Cmd = cmd, Args = args == null ? null : IpcJson.ToNode(args) as JsonObject };
            await WriteAsync(pipe, JsonSerializer.Serialize(req, IpcJson.Options), cts.Token);
            var text = await ReadAsync(pipe, cts.Token) ?? throw new IpcException("O serviço encerrou a conexão.");
            var resp = JsonSerializer.Deserialize<IpcResponse>(text, IpcJson.Options) ?? throw new IpcException("Resposta vazia.");
            if (!resp.Ok) throw new IpcException(resp.Error ?? "Erro desconhecido.");
            return resp.Data;
        }

        public async Task<T?> CallAsync<T>(string cmd, object? args = null, TimeSpan? timeout = null, CancellationToken ct = default)
            => IpcJson.From<T>(await CallAsync(cmd, args, timeout, ct));
    }
}
