using HirschNotify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HirschNotify.Pages;

[Authorize]
public class LogsModel : PageModel
{
    public void OnGet() { }

    public IActionResult OnGetTail(int lines = 200, string? level = null)
    {
        var logDir = AppPaths.LogsDir;
        if (!Directory.Exists(logDir))
        {
            return Content($"<p class='text-muted-msg'>No log directory found at {logDir}</p>", "text/html");
        }

        var latest = Directory.GetFiles(logDir, "HirschNotify-*.log")
            .OrderByDescending(f => f)
            .FirstOrDefault();

        if (latest == null)
        {
            return Content("<p class='text-muted-msg'>No log files found yet.</p>", "text/html");
        }

        var logLines = ReadLastLines(latest, lines);

        if (!string.IsNullOrEmpty(level))
        {
            logLines = logLines.Where(l => l.Contains($"] {level}"));
        }

        var html = new System.Text.StringBuilder();
        html.Append("<pre class='log-viewer'>");
        foreach (var line in logLines)
        {
            var cssClass = line switch
            {
                var l when l.Contains("] ERR") || l.Contains("] FTL") => "log-error",
                var l when l.Contains("] WRN") => "log-warn",
                var l when l.Contains("] INF") => "log-info",
                var l when l.Contains("] DBG") => "log-debug",
                _ => "log-line"
            };
            html.Append($"<span class='{cssClass}'>{System.Net.WebUtility.HtmlEncode(line)}</span>\n");
        }
        html.Append("</pre>");

        return Content(html.ToString(), "text/html");
    }

    private static IEnumerable<string> ReadLastLines(string path, int count)
    {
        // Stream the file since log files can be large
        var allLines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            allLines.Add(line);
        }
        return allLines.Count > count ? allLines.Skip(allLines.Count - count) : allLines;
    }
}
