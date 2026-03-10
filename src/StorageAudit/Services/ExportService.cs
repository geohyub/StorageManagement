namespace StorageAudit.Services;

using System.Text;
using System.Text.Json;
using StorageAudit.Models;

public class ExportService
{
    private readonly SqliteLogRepository _repo;
    private readonly string _exportFolder;

    public ExportService(SqliteLogRepository repo, string exportFolder)
    {
        _repo = repo;
        _exportFolder = exportFolder;
        Directory.CreateDirectory(_exportFolder);
    }

    public string ExportCsv(EventQuery query)
    {
        var events = _repo.GetAllEvents(query);
        var fileName = $"audit_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        var filePath = Path.Combine(_exportFolder, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("ID,Timestamp,ActionType,FileName,FullPath,OldPath,Direction,FileSizeBytes,Extension,DetectionBasis,Confidence,AlertLevel,UserAccount,IsSelfGenerated,GroupId,Notes");

        foreach (var e in events)
        {
            sb.AppendLine(string.Join(",",
                e.Id,
                CsvEscape(e.Timestamp.ToString("o")),
                e.ActionType,
                CsvEscape(e.FileName),
                CsvEscape(e.FullPath),
                CsvEscape(e.OldPath ?? ""),
                e.Direction,
                e.FileSizeBytes?.ToString() ?? "",
                CsvEscape(e.Extension ?? ""),
                CsvEscape(e.DetectionBasis ?? ""),
                e.Confidence,
                e.Alert,
                CsvEscape(e.UserAccount ?? ""),
                e.IsSelfGenerated,
                CsvEscape(e.GroupId ?? ""),
                CsvEscape(e.Notes ?? "")
            ));
        }
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return fileName;
    }

    public string ExportJson(EventQuery query)
    {
        var events = _repo.GetAllEvents(query);
        var fileName = $"audit_export_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var filePath = Path.Combine(_exportFolder, fileName);
        var json = JsonSerializer.Serialize(new
        {
            ExportTimestamp = DateTime.UtcNow,
            TotalEvents = events.Count,
            Events = events
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json, Encoding.UTF8);
        return fileName;
    }

    public string ExportHtml(EventQuery query)
    {
        var events = _repo.GetAllEvents(query);
        var stats = _repo.GetStats(query.From, query.To);
        var fileName = $"audit_report_{DateTime.Now:yyyyMMdd_HHmmss}.html";
        var filePath = Path.Combine(_exportFolder, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"ko\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine("<title>Storage Audit Report</title><style>");
        sb.AppendLine("*{margin:0;padding:0;box-sizing:border-box}");
        sb.AppendLine("body{font-family:'Segoe UI',sans-serif;background:#0f172a;color:#e2e8f0;padding:24px}");
        sb.AppendLine("h1{color:#38bdf8;margin-bottom:8px}.meta{color:#94a3b8;margin-bottom:24px}");
        sb.AppendLine(".stats{display:flex;gap:16px;margin-bottom:24px;flex-wrap:wrap}");
        sb.AppendLine(".stat{background:#1e293b;border-radius:8px;padding:16px 24px;min-width:140px}");
        sb.AppendLine(".stat .lbl{font-size:12px;color:#94a3b8;text-transform:uppercase}");
        sb.AppendLine(".stat .val{font-size:28px;font-weight:bold}");
        sb.AppendLine("table{width:100%;border-collapse:collapse;font-size:13px}");
        sb.AppendLine("th{background:#1e293b;color:#94a3b8;text-align:left;padding:10px 12px;font-size:11px;text-transform:uppercase}");
        sb.AppendLine("td{padding:8px 12px;border-bottom:1px solid #1e293b}tr:hover{background:#1e293b}");
        sb.AppendLine(".w{color:#f59e0b}.d{color:#ef4444}.s{opacity:.5}");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h1>Storage Audit Report</h1>");
        sb.AppendLine($"<p class=\"meta\">Generated: {HtmlEscape(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))} | Events: {events.Count}</p>");
        sb.AppendLine("<div class=\"stats\">");
        sb.AppendLine($"<div class=\"stat\"><div class=\"lbl\">Total</div><div class=\"val\">{stats.TotalEvents}</div></div>");
        sb.AppendLine($"<div class=\"stat\"><div class=\"lbl\">Imports</div><div class=\"val\" style=\"color:#06b6d4\">{stats.ImportCount}</div></div>");
        sb.AppendLine($"<div class=\"stat\"><div class=\"lbl\">Exports</div><div class=\"val\" style=\"color:#f59e0b\">{stats.ExportCount}</div></div>");
        sb.AppendLine($"<div class=\"stat\"><div class=\"lbl\">Deletes</div><div class=\"val\" style=\"color:#ef4444\">{stats.DeleteCount}</div></div>");
        sb.AppendLine($"<div class=\"stat\"><div class=\"lbl\">Warnings</div><div class=\"val\" style=\"color:#ef4444\">{stats.WarningCount}</div></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("<table><thead><tr><th>Time</th><th>Action</th><th>File</th><th>Path</th><th>Direction</th><th>Size</th><th>Alert</th><th>Notes</th></tr></thead><tbody>");

        foreach (var e in events)
        {
            var cls = e.IsSelfGenerated ? " class=\"s\"" : e.Alert >= AlertLevel.Warning ? " class=\"w\"" : "";
            sb.AppendLine($"<tr{cls}><td>{HtmlEscape(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))}</td>");
            sb.AppendLine($"<td>{HtmlEscape(e.ActionType.ToString())}</td><td>{HtmlEscape(e.FileName)}</td>");
            sb.AppendLine($"<td>{HtmlEscape(e.FullPath)}</td><td>{HtmlEscape(e.Direction.ToString())}</td>");
            sb.AppendLine($"<td>{FormatSize(e.FileSizeBytes)}</td><td>{HtmlEscape(e.Alert.ToString())}</td>");
            sb.AppendLine($"<td>{HtmlEscape(e.Notes ?? "")}</td></tr>");
        }
        sb.AppendLine("</tbody></table></body></html>");
        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        return fileName;
    }

    public byte[] GetExportFile(string fileName)
    {
        // 경로 순회 방지: 파일명에 디렉토리 구분자가 포함되면 거부
        if (fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar) ||
            fileName.Contains(".."))
            throw new ArgumentException("Invalid file name");

        var path = Path.Combine(_exportFolder, fileName);
        var fullPath = Path.GetFullPath(path);

        // 최종 경로가 export 폴더 내부인지 확인
        if (!fullPath.StartsWith(Path.GetFullPath(_exportFolder), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access denied");

        return File.ReadAllBytes(fullPath);
    }

    private static string FormatSize(long? bytes)
    {
        if (!bytes.HasValue) return "-";
        double b = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return $"{b:F1} {units[i]}";
    }

    private static string CsvEscape(string val) =>
        val.Contains(',') || val.Contains('"') || val.Contains('\n')
            ? $"\"{val.Replace("\"", "\"\"")}\"" : val;

    private static string HtmlEscape(string val) =>
        val.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
