
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FoodMenu_Extract.Services
{
    public record SearchResultItem(string Title, string Link, string Snippet, string DisplayLink)
    {
        public string Host
        {
            get
            {
                if (Uri.TryCreate(Link, UriKind.Absolute, out var u))
                    return u.Host.Replace("www.", "").ToLowerInvariant();
                return string.Empty;
            }
        }

        public string Path
        {
            get
            {
                if (Uri.TryCreate(Link, UriKind.Absolute, out var u))
                    return u.AbsolutePath.ToLowerInvariant();
                return string.Empty;
            }
        }
    }

    public class GoogleSearchClient
    {
        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly string _cx;
        private readonly int _numResults;

        private static readonly HashSet<string> BookingBlacklist = new(StringComparer.OrdinalIgnoreCase)
        {
            "booking.com","expedia","tripadvisor","hotels.com","agoda","facebook.com","yelp.com","opentable.com","zomato.com","foursquare.com"
        };

        private static readonly string[] MenuKeywords = new[] { "menu", "menus", "dining", "restaurant", "menu.pdf", "menus.pdf", "dining/" };

        // Brand -> representative domain to prefer when brand token detected in hotel name
        private static readonly Dictionary<string, string> BrandDomains = new(StringComparer.OrdinalIgnoreCase)
        {
            { "hilton", "hilton.com" },
            { "marriott", "marriott.com" },
            { "hyatt", "hyatt.com" },
            { "ihg", "ihg.com" },
            { "radisson", "radissonhotels.com" },
            { "accor", "accor.com" },
            { "choice", "choicehotels.com" },
            { "wyndham", "wyndhamhotels.com" },
            { "ritz", "theritzcarlton.com" },
            { "fourseasons", "fourseasons.com" }
        };

        public GoogleSearchClient(HttpClient http, string apiKey, string cx, int numResults = 5)
        {
            _http = http;
            _apiKey = apiKey;
            _cx = cx;
            _numResults = Math.Clamp(numResults, 1, 10);
        }

        public async Task<List<SearchResultItem>> SearchAsync(string query)
        {
            var url = $"https://www.googleapis.com/customsearch/v1?key={Uri.EscapeDataString(_apiKey)}&cx={Uri.EscapeDataString(_cx)}&q={Uri.EscapeDataString(query)}&num={_numResults}";
            using var resp = await _http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(s);

            var items = new List<SearchResultItem>();
            if (doc.RootElement.TryGetProperty("items", out var jsonItems) && jsonItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in jsonItems.EnumerateArray())
                {
                    var title = it.GetPropertyOrDefault("title");
                    var link = it.GetPropertyOrDefault("link");
                    var snippet = it.GetPropertyOrDefault("snippet");
                    var displayLink = it.GetPropertyOrDefault("displayLink");
                    items.Add(new SearchResultItem(title, link, snippet, displayLink));
                }
            }
            return items;
        }

        // Best-effort candidates prioritized by:
        //  - brand domain present in hotel name (e.g., "hilton")
        //  - URL path contains menu/dining keywords ("/dining", "/menus", "menu")
        //  - title/snippet mentions menu/dining
        // Blacklists common OTA/reservation domains (opentable, yelp, etc.)
        public async Task<List<SearchResultItem>> FindBestMenuCandidatesAsync(string hotelName, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(hotelName)) return new List<SearchResultItem>();

            var tokens = Tokenize(hotelName);
            string? brandDomain = null;
            foreach (var kv in BrandDomains)
            {
                // check token presence (normalized)
                if (tokens.Contains(kv.Key.Replace(" ", "")))
                {
                    brandDomain = kv.Value;
                    break;
                }
            }

            var queries = new List<string>
            {
                $"{hotelName} official site",
                $"{hotelName} menu",
                $"{hotelName} dining",
                $"{hotelName} \"restaurant\""
            };

            if (!string.IsNullOrEmpty(brandDomain))
            {
                queries.Insert(0, $"site:{brandDomain} {hotelName} menu");
                queries.Add($"site:{brandDomain} {hotelName} dining");
            }

            var results = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
            var queryLimit = Math.Min(queries.Count, 4); // limit API calls

            for (int i = 0; i < queryLimit; i++)
            {
                var q = queries[i];
                List<SearchResultItem> items;
                try
                {
                    items = await SearchAsync(q);
                }
                catch
                {
                    continue;
                }

                foreach (var it in items)
                {
                    if (string.IsNullOrWhiteSpace(it.Link)) continue;
                    if (Uri.TryCreate(it.Link, UriKind.Absolute, out var u))
                    {
                        var host = u.Host.Replace("www.", "");
                        if (BookingBlacklist.Contains(host)) continue;
                    }
                    if (!results.ContainsKey(it.Link))
                        results[it.Link] = it;
                }
            }

            // fallback
            if (results.Count == 0)
            {
                try
                {
                    var fallback = await SearchAsync(hotelName + " menu");
                    foreach (var it in fallback)
                    {
                        if (string.IsNullOrWhiteSpace(it.Link)) continue;
                        if (Uri.TryCreate(it.Link, UriKind.Absolute, out var u))
                        {
                            var host = u.Host.Replace("www.", "");
                            if (BookingBlacklist.Contains(host)) continue;
                        }
                        if (!results.ContainsKey(it.Link))
                            results[it.Link] = it;
                    }
                }
                catch { }
            }

            // Score and order
            var scored = new List<(SearchResultItem Item, int Score)>();
            foreach (var it in results.Values)
            {
                var score = 0;
                var host = it.Host;
                var path = it.Path;

                if (!string.IsNullOrEmpty(brandDomain) && host.Contains(brandDomain.Replace("www.", "").Split('.')[0]))
                    score += 60;

                if (tokens.Any(tok => !string.IsNullOrWhiteSpace(tok) && host.Contains(tok)))
                    score += 40;

                if (MenuKeywords.Any(k => path.Contains(k)))
                    score += 50;

                if (MenuKeywords.Any(k => (it.Title ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0
                                       || (it.Snippet ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                    score += 25;

                if (!string.IsNullOrWhiteSpace(it.DisplayLink) && tokens.Any(tok => it.DisplayLink.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0))
                    score += 10;

                if (host.Contains("opentable") || host.Contains("yelp") || host.Contains("tripadvisor"))
                    score -= 50;

                if (path.Contains("/dining") || path.Contains("/restaurant") || path.Contains("/restaurants") || path.Contains("/menus"))
                    score += 20;

                scored.Add((it, score));
            }

            var ordered = scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Item.Title).Select(s => s.Item).Take(maxResults).ToList();
            return ordered;
        }

        private static string[] Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            var tokens = Regex.Split(text.ToLowerInvariant(), @"\W+")
                .Where(s => s.Length >= 3)
                .Select(s => s.Trim())
                .Distinct()
                .ToArray();
            return tokens;
        }
    }

    internal static class JsonExtensions
    {
        public static string GetPropertyOrDefault(this JsonElement e, string name)
        {
            if (e.TryGetProperty(name, out var prop) && prop.ValueKind != JsonValueKind.Null)
            {
                return prop.GetString() ?? string.Empty;
            }
            return string.Empty;
        }
    }
}