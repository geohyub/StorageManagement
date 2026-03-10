using System.Text.Json;
using System.Text.Json.Serialization;
using StorageAudit.Models;
using StorageAudit.Services;

// single-file 앱에서는 AppContext.BaseDirectory가 임시 폴더를 가리킬 수 있음
// Environment.ProcessPath로 실제 exe 위치를 사용
var exeDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDirectory,
    WebRootPath = Path.Combine(exeDirectory, "wwwroot")
});

builder.Services.AddSingleton<AuditEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditEngine>());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var configPort = 19840;
try
{
    var cfgExeDir = (Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    foreach (var candidate in new[] { cfgExeDir, Directory.GetParent(cfgExeDir)?.FullName ?? cfgExeDir })
    {
        var cp = Path.Combine(candidate, ".storageaudit", "config.json");
        if (File.Exists(cp))
        {
            var loaded = JsonSerializer.Deserialize<AuditConfig>(File.ReadAllText(cp));
            if (loaded != null) { configPort = loaded.WebPort; break; }
        }
    }
}
catch { /* use default */ }

builder.WebHost.ConfigureKestrel(options =>
{
    // localhost만 바인딩하여 네트워크 노출 방지
    options.ListenLocalhost(configPort);
});

builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() },
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

app.MapGet("/api/status", (AuditEngine engine) => Results.Json(new
{
    isRunning = engine.IsRunning,
    watchRoot = engine.WatchRoot,
    machineName = engine.MachineName,
    storageName = engine.StorageName,
    port = engine.Config.WebPort,
    ignorePatterns = engine.Config.IgnorePatterns,
    connectedDrives = (engine.DriveWatcher?.ActiveDrives ?? new Dictionary<string, SecondaryDriveInfo>())
        .Select(d => new { root = d.Key, label = d.Value.Label, type = d.Value.DriveType.ToString(), connectedAt = d.Value.ConnectedAt })
        .ToArray()
}, jsonOpts));

app.MapGet("/api/stats", (AuditEngine engine, DateTime? from, DateTime? to) =>
{
    if (engine.Repository == null) return Results.Problem("Engine not ready");
    return Results.Json(engine.Repository.GetStats(from, to), jsonOpts);
});

app.MapGet("/api/events", (AuditEngine engine, string? search, int? actionType,
    int? minAlert, DateTime? from, DateTime? to, bool? includeSelf,
    int? page, int? pageSize, string? sortBy, bool? sortDesc) =>
{
    if (engine.Repository == null) return Results.Problem("Engine not ready");
    var query = new EventQuery
    {
        Search = search,
        ActionType = actionType.HasValue ? (FileActionType)actionType.Value : null,
        MinAlertLevel = minAlert.HasValue ? (AlertLevel)minAlert.Value : null,
        From = from,
        To = to,
        IncludeSelfGenerated = includeSelf ?? false,
        Page = Math.Max(1, page ?? 1),
        PageSize = Math.Clamp(pageSize ?? 100, 1, 1000),
        SortBy = sortBy ?? "Timestamp",
        SortDesc = sortDesc ?? true
    };
    return Results.Json(engine.Repository.Query(query), jsonOpts);
});

app.MapPost("/api/export/{format}", (AuditEngine engine, string format,
    DateTime? from, DateTime? to, int? actionType, bool? includeSelf) =>
{
    if (engine.Export == null) return Results.Problem("Engine not ready");

    // format 화이트리스트 검증
    var allowedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "csv", "json", "html" };
    if (!allowedFormats.Contains(format))
        return Results.BadRequest(new { error = "Supported formats: csv, json, html" });

    var query = new EventQuery
    {
        From = from,
        To = to,
        ActionType = actionType.HasValue ? (FileActionType)actionType.Value : null,
        IncludeSelfGenerated = includeSelf ?? false
    };
    var fileName = format.ToLower() switch
    {
        "csv" => engine.Export.ExportCsv(query),
        "json" => engine.Export.ExportJson(query),
        "html" => engine.Export.ExportHtml(query),
        _ => throw new InvalidOperationException()
    };
    return Results.Json(new { fileName, downloadUrl = $"/api/export/download/{Uri.EscapeDataString(fileName)}" }, jsonOpts);
});

app.MapGet("/api/export/download/{fileName}", (AuditEngine engine, string fileName) =>
{
    if (engine.Export == null) return Results.Problem("Engine not ready");
    try
    {
        var data = engine.Export.GetExportFile(fileName);
        var ct = fileName.EndsWith(".csv") ? "text/csv"
            : fileName.EndsWith(".json") ? "application/json"
            : fileName.EndsWith(".html") ? "text/html"
            : "application/octet-stream";
        return Results.File(data, ct, fileName);
    }
    catch (FileNotFoundException) { return Results.NotFound(); }
    catch (UnauthorizedAccessException) { return Results.Forbid(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});

app.MapPost("/api/config/watchroot", (AuditEngine engine, WatchRootRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Path))
        return Results.BadRequest(new { error = "Path is required" });
    try
    {
        engine.UpdateWatchRoot(req.Path);
        return Results.Ok(new { success = true, watchRoot = engine.WatchRoot });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.MapPost("/api/config/ignorepatterns", (AuditEngine engine, IgnorePatternsRequest req) =>
{
    if (req.Patterns == null || req.Patterns.Count == 0)
        return Results.BadRequest(new { error = "At least one pattern is required" });
    engine.UpdateIgnorePatterns(req.Patterns);
    return Results.Ok(new { success = true });
});

Console.WriteLine($@"
  ========================================================
       Storage Audit - Portable File Activity Logger
  ========================================================
    Dashboard : http://localhost:{configPort}
    Press Ctrl+C to stop monitoring
  ========================================================
");

app.Run();

record WatchRootRequest(string Path);
record IgnorePatternsRequest(List<string> Patterns);
