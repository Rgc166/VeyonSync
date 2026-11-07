using System.Net.Http.Json;
using VeyonSync.Shared;
using VeyonSync.Student;

// Atualizador auxiliar (executado a partir de uma cópia fora da pasta de instalação).
if (args.Length == 4 && args[0] == "--apply-release")
    return UpdateRunner.Run("Student", args[1], args[2], args[3]);
if (args.Length == 5 && args[0] == "--apply-update")
    return UpdateAgent.ApplyMode(args[1], long.Parse(args[2]), args[3], args[4]);

// Comandos da Etapa 1 preservados (usados por scripts antigos).
if (args.Length == 2 && args[0] == "--pair")
{
    var cfg = AgentConfig.Load();
    var probe = new Tls.Probe();
    using var h = Tls.CreateClient(cfg.TeacherCertSha256, probe);
    try
    {
        var r = await h.PostAsJsonAsync(cfg.TeacherUrl.TrimEnd('/') + "/api/pair",
            new PairRequest(args[1], cfg.AgentId, cfg.Lab, Environment.MachineName, string.IsNullOrEmpty(cfg.Position) ? null : cfg.Position, AppInfo.Version, cfg.ReceiverId));
        if (!r.IsSuccessStatusCode)
        {
            string? msg = null;
            try { msg = (await r.Content.ReadFromJsonAsync<ApiError>())?.Error; } catch (Exception) { }
            Console.WriteLine($"Pareamento recusado ({(int)r.StatusCode}). {msg}");
            return 1;
        }
        var p = (await r.Content.ReadFromJsonAsync<PairResponse>())!;
        cfg.SetToken(p.Token);
        if (p.ReceiverId != null) cfg.ReceiverId = p.ReceiverId;
        if (p.TeacherName != null) cfg.TeacherName = p.TeacherName;
        if (!string.IsNullOrEmpty(p.UpdatePublicKey) && string.IsNullOrEmpty(cfg.UpdatePublicKey)) cfg.UpdatePublicKey = p.UpdatePublicKey;
        cfg.Save();
        Console.WriteLine("Pareado com sucesso.");
        return 0;
    }
    catch (Exception e) { Console.WriteLine("Falha de conexão/certificado: " + Tls.Explain(e, probe)); return 1; }
}
if (args.Length == 1 && args[0] == "--send-now")
{
    Directory.CreateDirectory(AgentConfig.Dir);
    File.WriteAllText(Agent.ForceFlag, "1");
    Console.WriteLine("Envio solicitado.");
    return 0;
}
if (args.Length == 2 && args[0] == "--import-link")
{
    // Usado pelo instalador: importa o .vsync sem precisar abrir a interface.
    try
    {
        var link = LinkFile.Load(args[1]);
        var cfg = AgentConfig.Load();
        if (cfg.Paired && cfg.ReceiverId != null && cfg.ReceiverId != link.ReceiverId)
        {
            Console.WriteLine("Este PC já está ligado a outro professor. Use a interface STD para trocar (ação administrativa explícita).");
            return 2;
        }
        cfg.TeacherUrl = link.Url; cfg.Lab = link.LabId; cfg.LabName = link.LabName; cfg.TeacherName = link.TeacherName;
        cfg.ReceiverId = link.ReceiverId; cfg.TeacherCertSha256 = link.CertificateSha256;
        if (!string.IsNullOrEmpty(link.UpdatePublicKey)) cfg.UpdatePublicKey = link.UpdatePublicKey;
        cfg.LinkImportedAt = DateTimeOffset.Now;
        cfg.Save();
        Console.WriteLine($"Ligação importada: laboratório {link.LabId}, professor {link.TeacherName}.");
        return 0;
    }
    catch (Exception e) { Console.WriteLine("Arquivo de ligação recusado: " + e.Message); return 1; }
}
if (args.Length == 1 && args[0] == "--version") { Console.WriteLine(AppInfo.Version); return 0; }

var b = Host.CreateApplicationBuilder(args);
b.Services.AddWindowsService(o => o.ServiceName = AppInfo.StudentService);
b.Services.AddSingleton<Agent>();
b.Services.AddSingleton<UpdateAgent>();
b.Services.AddSingleton(sp => new ReleaseService("Student", m => sp.GetRequiredService<ILogger<ReleaseService>>().LogInformation("{M}", m)));
b.Services.AddHostedService<AgentWorker>();
b.Build().Run();
return 0;
