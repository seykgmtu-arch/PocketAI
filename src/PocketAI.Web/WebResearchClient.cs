using System.Net;
using System.Text.RegularExpressions;

namespace PocketAI.Web;
public sealed record WebSearchResult(string Title, string Url, string Snippet);
public sealed record WebPageExcerpt(string Title, string Url, string Text);

public sealed class WebResearchClient : IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
    public WebResearchClient() => _http.DefaultRequestHeaders.UserAgent.ParseAdd("PocketAI/0.3 local-research-client");
    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, int maxResults, CancellationToken ct=default)
    {
        var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
        var html = await _http.GetStringAsync(url, ct); var list = new List<WebSearchResult>();
        var rx = new Regex("<a[^>]+class=\\\"result__a\\\"[^>]+href=\\\"(?<u>[^\\\"]+)\\\"[^>]*>(?<t>.*?)</a>", RegexOptions.IgnoreCase|RegexOptions.Singleline);
        foreach(Match m in rx.Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups["u"].Value); var title = Strip(m.Groups["t"].Value);
            if (href.Contains("uddg=")) { var mm=Regex.Match(href,@"[?&]uddg=([^&]+)"); if(mm.Success) href=Uri.UnescapeDataString(mm.Groups[1].Value); }
            if(Uri.TryCreate(href,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https") list.Add(new(title,uri.ToString(),""));
            if(list.Count>=maxResults) break;
        }
        return list;
    }
    public async Task<WebPageExcerpt> ReadAsync(string url, int maxChars, CancellationToken ct=default)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Разрешены только HTTP/HTTPS URL.");
        var html=await _http.GetStringAsync(uri,ct); var title=Regex.Match(html,"<title[^>]*>(.*?)</title>",RegexOptions.IgnoreCase|RegexOptions.Singleline);
        var clean=Regex.Replace(html,"<(script|style|noscript)[^>]*>.*?</\\1>"," ",RegexOptions.IgnoreCase|RegexOptions.Singleline); clean=Strip(clean); if(clean.Length>maxChars)clean=clean[..maxChars];
        return new(title.Success?Strip(title.Groups[1].Value):uri.Host,uri.ToString(),clean);
    }
    private static string Strip(string s){s=Regex.Replace(s,"<[^>]+>"," ");s=WebUtility.HtmlDecode(s);return Regex.Replace(s,@"\s+"," ").Trim();}
    public void Dispose()=>_http.Dispose();
}
