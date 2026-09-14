using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using PocketAI.Core.Configuration;
using PocketAI.Hardware;

namespace PocketAI.Inference;

public sealed class EmbeddingServerManager : IDisposable
{
    private readonly string _baseDirectory; private readonly PocketAiConfig _config;
    private Process? _process; private LlamaServerSession? _session;
    public EmbeddingServerManager(string baseDirectory, PocketAiConfig config) { _baseDirectory = baseDirectory; _config = config; }
    public bool IsConfigured => _config.Embeddings.Enabled && File.Exists(ConfigLoader.ResolvePath(_baseDirectory, _config.Embeddings.ModelPath));
    public LlamaServerSession? Session => _session;

    public async Task<LlamaServerSession> StartAsync(HardwareProfile hardware, CancellationToken ct = default)
    {
        if (_process is { HasExited:false } && _session is not null) return _session;
        var model = ConfigLoader.ResolvePath(_baseDirectory, _config.Embeddings.ModelPath);
        if (!File.Exists(model)) throw new FileNotFoundException("Embedding GGUF не найден.", model);
        var cuda = ConfigLoader.ResolvePath(_baseDirectory, _config.Runtime.CudaPath);
        var cpu = ConfigLoader.ResolvePath(_baseDirectory, _config.Runtime.CpuPath);
        var exe = hardware.HasNvidiaGpu && File.Exists(cuda) ? cuda : cpu;
        if (!File.Exists(exe)) throw new FileNotFoundException("llama-server.exe для embeddings не найден.", exe);
        var backend = hardware.HasNvidiaGpu && exe.Equals(cuda, StringComparison.OrdinalIgnoreCase) ? BackendKind.Cuda : BackendKind.Cpu;
        var port = ReservePort(); var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(); const string alias="pocket-embedding";
        var psi = new ProcessStartInfo { FileName=exe, WorkingDirectory=Path.GetDirectoryName(exe)!, UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        foreach (var arg in new[]{"--model",model,"--host","127.0.0.1","--port",port.ToString(),"--ctx-size",_config.Embeddings.ContextSize.ToString(),"--alias",alias,"--api-key",key,"--no-webui","--offline","--embedding","--pooling","mean","--n-gpu-layers",backend==BackendKind.Cuda?"all":"0"}) psi.ArgumentList.Add(arg);
        _process = new Process { StartInfo=psi }; if(!_process.Start()) throw new InvalidOperationException("Не удалось запустить embedding llama-server.");
        _ = _process.StandardOutput.ReadToEndAsync(ct); _ = _process.StandardError.ReadToEndAsync(ct);
        var uri = new Uri($"http://127.0.0.1:{port}/"); await WaitAsync(uri, ct); _session = new LlamaServerSession(uri,key,backend,port,alias); return _session;
    }
    private async Task WaitAsync(Uri uri, CancellationToken ct)
    { using var http=new HttpClient{BaseAddress=uri,Timeout=TimeSpan.FromSeconds(2)}; var until=DateTime.UtcNow.AddMinutes(2); while(DateTime.UtcNow<until){ct.ThrowIfCancellationRequested(); if(_process is {HasExited:true}) throw new InvalidOperationException("Embedding server завершился."); try{using var r=await http.GetAsync("health",ct);if(r.IsSuccessStatusCode)return;}catch{} await Task.Delay(300,ct);} throw new TimeoutException("Embedding server не готов."); }
    private static int ReservePort(){var l=new TcpListener(IPAddress.Loopback,0);l.Start();try{return ((IPEndPoint)l.LocalEndpoint).Port;}finally{l.Stop();}}
    public void Dispose(){try{if(_process is {HasExited:false})_process.Kill(true);}catch{} _process?.Dispose();_process=null;_session=null;}
}
