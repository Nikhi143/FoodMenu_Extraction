using FoodMenu_Extract.Models;
using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
using UglyToad.PdfPig;

namespace FoodMenu_Extract.Services
{
    public class MenuExtractionResult
    {
        public bool Found { get; set; }
        public string SourceUrl { get; set; } = string.Empty;
        public string RawText { get; set; } = string.Empty;
        public StructuredMenu StructuredMenu { get; set; } = new StructuredMenu();
    }

    public class MenuExtractor
    {
        private readonly HttpClient _http;
        private readonly bool _enablePageCrawl;
        private readonly int _maxPageBytes;
        private readonly HeadlessRenderer? _renderer;
        private readonly bool _useHeadlessRender;

        // Add selector hints used to wait for dynamic content
        private static readonly string[] RenderWaitSelectors = new[]
        {
            "[data-testid*='menu']",
            "[data-testid*='menu-item']",
            ".menu",
            ".menus",
            ".dining",
            ".menu-item",
            ".tab-panel",
            ".price",
            ".menu-section",
            ".grid",
            ".card"
        };

        private static readonly string[] MenuKeywords = new[] { "menu", "menus", "dining", "restaurant", "breakfast", "lunch", "dinner", "room service", "à la carte", "à-la-carte" };
        private static readonly Regex PriceRegex = new(@"\p{Sc}?\s*\d+([.,]\d{1,2})?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // class/id token heuristics
        private static readonly string[] NameClassTokens = new[] { "name", "title", "dish", "item", "menu-item", "menu-title", "dish-title", "menu-name" };
        private static readonly string[] PriceClassTokens = new[] { "price", "amount", "cost", "rate", "menu-price", "dish-price", "price-text", "text-right" };
        private static readonly string[] SectionClassTokens = new[] { "section", "heading", "title", "group", "category", "tab-panel" };

        public MenuExtractor(HttpClient http, bool enablePageCrawl = true, int maxPageBytes = 2_000_000, HeadlessRenderer? renderer = null)
        {
            _http = http;
            _enablePageCrawl = enablePageCrawl;
            _maxPageBytes = maxPageBytes;
            _renderer = renderer;
            _useHeadlessRender = renderer != null;
        }

        // Top-level attempt: static fetch (scan JSON-LD, HTML, PDF links). If enabled, render and re-scan.
        public async Task<MenuExtractionResult> TryExtractMenuFromCandidateAsync(string url)
        {
            try
            {
                // Try static fetch and scan
                var staticResult = await TryFetchAndScanUrlAsync(url);
                if (staticResult.Found) return staticResult;

                // If the page might be dynamic, optionally render and rescan (wait for likely selectors)
                if (_useHeadlessRender)
                {
                    var rendered = await _renderer!.RenderPageAsync(url, 45000);
                    if (!string.IsNullOrEmpty(rendered))
                    {
                        var renderedScan = await TryScanHtmlStringAsync(rendered, url, isRendered: true);
                        if (renderedScan.Found) return renderedScan;

                        // Additional heuristic: grid/div based extraction on rendered DOM
                        var gridResult = ExtractFromDivGrid(rendered, url);
                        if (gridResult.Found) return gridResult;

                        // Additional heuristic: price-based layout pairing on rendered DOM
                        var priceBased = ExtractByPriceLayout(rendered, url);
                        if (priceBased.Found) return priceBased;
                    }
                }

                // If enabled, try crawling homepage links for menu keywords (static)
                if (_enablePageCrawl)
                {
                    try
                    {
                        var baseUri = new Uri(url);
                        var homepageUrl = baseUri.GetLeftPart(UriPartial.Authority);
                        var homepage = await FetchHtmlAsync(homepageUrl);
                        if (homepage != null)
                        {
                            var doc = new HtmlDocument();
                            doc.LoadHtml(homepage);

                            var links = doc.DocumentNode.SelectNodes("//a[@href]")?
                                .Select(a => a.Attributes["href"]?.Value)
                                .Where(h => !string.IsNullOrWhiteSpace(h))
                                .Select(h => MakeAbsoluteUrl(homepageUrl, h!))
                                .Distinct()
                                .ToList() ?? new List<string>();

                            // Prefer links containing menu keywords; also allow pdf links
                            foreach (var link in links.Where(l => MenuKeywords.Any(k => l.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) || l.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                            {
                                var res = await TryFetchAndScanUrlAsync(link);
                                if (res.Found) return res;

                                if (_useHeadlessRender)
                                {
                                    var renderedHtml = await _renderer!.RenderPageAsync(link, 30000);
                                    if (!string.IsNullOrEmpty(renderedHtml))
                                    {
                                        var r = await TryScanHtmlStringAsync(renderedHtml, link, isRendered: true);
                                        if (r.Found) return r;

                                        var priceRes = ExtractByPriceLayout(renderedHtml, link);
                                        if (priceRes.Found) return priceRes;

                                        var gridRes = ExtractFromDivGrid(renderedHtml, link);
                                        if (gridRes.Found) return gridRes;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // swallow crawl errors, continue
                    }
                }

                return new MenuExtractionResult { Found = false };
            }
            catch
            {
                return new MenuExtractionResult { Found = false };
            }
        }

        private async Task<MenuExtractionResult> TryFetchAndScanUrlAsync(string url)
        {
            var html = await FetchHtmlAsync(url);
            if (string.IsNullOrEmpty(html)) return new MenuExtractionResult { Found = false };

            // If page contains no menu keywords at all, skip (fast)
            if (!MenuKeywords.Any(k => html.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) &&
                !html.Contains(".pdf", StringComparison.OrdinalIgnoreCase) &&
                !html.Contains("application/ld+json", StringComparison.OrdinalIgnoreCase))
            {
                return new MenuExtractionResult { Found = false };
            }

            // Scan HTML string (includes JSON-LD detection and conventional extraction)
            var scan = await TryScanHtmlStringAsync(html, url, isRendered: false);
            if (scan.Found) return scan;

            // Grid/div extraction (covers flex/grid layouts that don't use lists/tables)
            var grid = ExtractFromDivGrid(html, url);
            if (grid.Found) return grid;

            // Look for PDF links on the page and attempt extraction
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                var pdfLinks = doc.DocumentNode.SelectNodes("//a[@href]")?
                    .Select(a => a.Attributes["href"]?.Value)
                    .Where(h => !string.IsNullOrWhiteSpace(h) && h!.IndexOf(".pdf", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(h => MakeAbsoluteUrl(url, h!))
                    .Distinct()
                    .ToList() ?? new List<string>();

                // prefer menu.pdf etc.
                var ordered = pdfLinks
                    .OrderByDescending(l => l.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    .ToList();

                foreach (var pdfUrl in ordered)
                {
                    var pdfResult = await TryExtractFromPdfAsync(pdfUrl);
                    if (pdfResult.Found) return pdfResult;
                }
            }
            catch
            {
                // ignore pdf parse errors
            }

            return new MenuExtractionResult { Found = false };
        }

        // Scan HTML string for JSON-LD structured menus or conventional HTML menu structures.
        private async Task<MenuExtractionResult> TryScanHtmlStringAsync(string html, string sourceUrl, bool isRendered)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            // 1) JSON-LD detection
            var jsonLdNodes = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
            if (jsonLdNodes != null)
            {
                foreach (var node in jsonLdNodes)
                {
                    var raw = node.InnerText?.Trim();
                    if (string.IsNullOrEmpty(raw)) continue;

                    try
                    {
                        using var jdoc = JsonDocument.Parse(raw);
                        var menu = ExtractStructuredMenuFromJsonElement(jdoc.RootElement, sourceUrl);
                        if (menu != null && menu.Sections.Any())
                        {
                            return new MenuExtractionResult
                            {
                                Found = true,
                                SourceUrl = sourceUrl,
                                RawText = GetPlainText(doc.DocumentNode),
                                StructuredMenu = menu
                            };
                        }
                    }
                    catch (JsonException)
                    {
                        // skip invalid JSON-LD blocks
                        continue;
                    }
                }
            }

            // 2) HTML-based extraction (existing heuristics)
            // Strategy: look for nodes with id/class containing 'menu' or 'restaurant'
            var menuNodes = doc.DocumentNode
                .SelectNodes("//*[contains(translate(@id,'MENU','menu'),'menu') or contains(translate(@class,'MENU','menu'),'menu') or contains(translate(@id,'RESTAURANT','restaurant'),'restaurant') or contains(translate(@class,'RESTAURANT','restaurant'),'restaurant')]")
                ?.ToList();

            if (menuNodes == null || menuNodes.Count == 0)
            {
                var candidates = doc.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4");
                if (candidates != null)
                {
                    foreach (var h in candidates)
                    {
                        var text = h.InnerText?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (MenuKeywords.Any(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            var sibling = h.SelectSingleNode("following-sibling::*[1]");
                            if (sibling != null && (sibling.Name == "ul" || sibling.Name == "ol" || sibling.Name == "table" || sibling.SelectSingleNode(".//li") != null))
                            {
                                menuNodes ??= new List<HtmlNode>();
                                menuNodes.Add(sibling);
                            }
                        }
                    }
                }
            }

            if (menuNodes == null || menuNodes.Count == 0)
            {
                var lists = doc.DocumentNode.SelectNodes("//ul|//ol|//table");
                if (lists != null)
                {
                    foreach (var node in lists)
                    {
                        var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty);
                        if (PriceRegex.IsMatch(text) && text.Split(new[] { '\n', '\r' }).Length > 1)
                        {
                            menuNodes ??= new List<HtmlNode>();
                            menuNodes.Add(node);
                        }
                    }
                }
            }

            if (menuNodes == null || menuNodes.Count == 0)
            {
                var body = doc.DocumentNode.SelectSingleNode("//body");
                if (body != null && MenuKeywords.Any(k => (body.InnerText ?? "").IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    var structured = new StructuredMenu { SourceUrl = sourceUrl };
                    structured.Sections.Add(new MenuSection { Name = "Page content", Items = ExtractMenuItemsFromText(body.InnerText ?? string.Empty) });
                    return new MenuExtractionResult
                    {
                        Found = true,
                        SourceUrl = sourceUrl,
                        RawText = GetPlainText(body),
                        StructuredMenu = structured
                    };
                }

                // no visible menu nodes found by previous heuristics
                return new MenuExtractionResult { Found = false };
            }

            // Build structured menu from found nodes
            var structuredMenu = new StructuredMenu { SourceUrl = sourceUrl };

            foreach (var node in menuNodes)
            {
                var sectionName = GuessSectionName(node) ?? "Menu";
                var items = new List<MenuItem>();

                var liNodes = node.SelectNodes(".//li");
                if (liNodes != null)
                {
                    foreach (var li in liNodes)
                    {
                        var txt = HtmlEntity.DeEntitize(li.InnerText)?.Trim() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(txt)) continue;
                        items.Add(new MenuItem { Name = ExtractItemName(txt), Description = ExtractDescription(txt), Price = ExtractPrice(txt) });
                    }
                }
                else if (node.Name == "table")
                {
                    var rows = node.SelectNodes(".//tr");
                    if (rows != null)
                    {
                        foreach (var tr in rows)
                        {
                            var cells = tr.SelectNodes(".//td|.//th");
                            if (cells == null) continue;
                            var cellTexts = cells.Select(c => HtmlEntity.DeEntitize(c.InnerText).Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                            if (cellTexts.Length == 0) continue;
                            var name = cellTexts.Length >= 1 ? cellTexts[0] : string.Empty;
                            var price = cellTexts.Length >= 2 ? cellTexts[1] : null;
                            items.Add(new MenuItem { Name = name, Price = price, Description = cellTexts.Length > 2 ? string.Join(" ", cellTexts.Skip(2)) : null });
                        }
                    }
                }
                else
                {
                    items.AddRange(ExtractMenuItemsFromText(node.InnerText));
                }

                if (items.Any())
                {
                    structuredMenu.Sections.Add(new MenuSection { Name = sectionName, Items = items });
                }
            }

            if (structuredMenu.Sections.Any())
            {
                var rawTextAggregate = string.Join("\n\n", structuredMenu.Sections.SelectMany(s => s.Items.Select(i => $"{i.Name} {i.Price ?? ""}")).Take(500));
                return new MenuExtractionResult
                {
                    Found = true,
                    SourceUrl = sourceUrl,
                    RawText = rawTextAggregate,
                    StructuredMenu = structuredMenu
                };
            }

            return new MenuExtractionResult { Found = false };
        }

        // NEW: Attempt to extract menus from div/grid/flex layouts (modern sites)
        private MenuExtractionResult ExtractFromDivGrid(string htmlOrRendered, string sourceUrl)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlOrRendered);

                // 1) Find candidate containers: class tokens, or containers with many element children
                var candidates = new List<HtmlNode>();

                // class-based candidates
                var classSelectors = new[] { "//div[contains(@class,'grid')]", "//div[contains(@class,'flex')]", "//section[contains(@class,'menu')]", "//div[contains(@class,'menu')]", "//div[contains(@class,'items')]", "//div[contains(@class,'cards')]" };
                foreach (var sel in classSelectors)
                {
                    var nodes = doc.DocumentNode.SelectNodes(sel);
                    if (nodes != null) candidates.AddRange(nodes);
                }

                // generic heavy containers: many child elements
                var genericDivs = doc.DocumentNode.SelectNodes("//div[count(*)>=3]");
                if (genericDivs != null)
                {
                    candidates.AddRange(genericDivs);
                }

                // dedupe
                candidates = candidates.Distinct().ToList();

                // score containers and try to extract items
                foreach (var container in candidates.OrderByDescending(c => c.InnerText?.Length ?? 0))
                {
                    // skip tiny containers
                    if ((container.InnerText?.Length ?? 0) < 100) continue;

                    var childElements = container.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToList();
                    // prefer containers with multiple children (likely repeating item elements)
                    if (childElements.Count < 2) continue;

                    // Count price occurrences inside container
                    var priceCount = PriceRegex.Matches(HtmlEntity.DeEntitize(container.InnerText ?? string.Empty)).Count;
                    if (priceCount < 2) continue; // need evidence of pricing

                    // Attempt structured extraction from repeating children
                    var items = ExtractItemsFromRepeatingChildren(container);
                    if (items != null && items.Count >= 3)
                    {
                        var sectionName = GuessSectionName(container) ?? GetContainerSectionName(container);
                        var structured = new StructuredMenu { SourceUrl = sourceUrl };
                        structured.Sections.Add(new MenuSection { Name = sectionName ?? "Menu", Items = items });
                        var raw = string.Join("\n", items.Select(i => $"{i.Name} {i.Price}"));
                        return new MenuExtractionResult { Found = true, SourceUrl = sourceUrl, RawText = raw, StructuredMenu = structured };
                    }

                    // Fallback: try traversal pairing within this container
                    var paired = ExtractPairByTraversal(container);
                    if (paired != null && paired.Count >= 3)
                    {
                        var sectionName = GuessSectionName(container) ?? GetContainerSectionName(container);
                        var structured = new StructuredMenu { SourceUrl = sourceUrl };
                        structured.Sections.Add(new MenuSection { Name = sectionName ?? "Menu", Items = paired });
                        var raw = string.Join("\n", paired.Select(i => $"{i.Name} {i.Price}"));
                        return new MenuExtractionResult { Found = true, SourceUrl = sourceUrl, RawText = raw, StructuredMenu = structured };
                    }
                }
            }
            catch
            {
                // ignore
            }

            return new MenuExtractionResult { Found = false };
        }

        // Extract items assuming each immediate child is a repeat (card/row) with name/price inside
        private List<MenuItem>? ExtractItemsFromRepeatingChildren(HtmlNode container)
        {
            var items = new List<MenuItem>();
            var childElements = container.ChildNodes.Where(n => n.NodeType == HtmlNodeType.Element).ToList();

            foreach (var child in childElements)
            {
                // try class-based price/name detection first
                var price = FindPriceByClass(child) ?? FindPriceInNode(child);
                var name = FindNameByClass(child) ?? FindBestTextCandidate(child);

                // If we have price but name is empty, attempt to look at preceding siblings for name
                if (string.IsNullOrWhiteSpace(name))
                {
                    var prev = child.PreviousSibling;
                    while (prev != null && (prev.NodeType != HtmlNodeType.Element)) prev = prev.PreviousSibling;
                    if (prev != null) name = FindBestTextCandidate(prev);
                }

                if (string.IsNullOrWhiteSpace(price) && string.IsNullOrWhiteSpace(name))
                {
                    // skip children that don't look like menu items
                    continue;
                }

                // If price exists but name absent, try immediate descendants for likely name text
                if (!string.IsNullOrWhiteSpace(price) && string.IsNullOrWhiteSpace(name))
                {
                    var lines = SplitToLines(HtmlEntity.DeEntitize(child.InnerText ?? string.Empty));
                    var candidate = lines.FirstOrDefault(l => !PriceRegex.IsMatch(l) && l.Length > 3);
                    name = candidate ?? name;
                }

                // If name exists but price absent, try to find price further inside the child or sibling
                if (string.IsNullOrWhiteSpace(price) && !string.IsNullOrWhiteSpace(name))
                {
                    price = FindPriceInNode(child) ?? FindPriceInNode(child.ParentNode);
                }

                // final cleanup
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(price)) continue;
                if (string.IsNullOrWhiteSpace(price) && string.IsNullOrWhiteSpace(name)) continue;

                var mi = new MenuItem
                {
                    Name = name?.Trim(),
                    Price = price?.Trim()
                };

                items.Add(mi);
            }

            return items.Count > 0 ? items : null;
        }

        // Pair by document-order traversal: accumulate name-like fragments, pair when encountering a price fragment
        private List<MenuItem>? ExtractPairByTraversal(HtmlNode container)
        {
            var items = new List<MenuItem>();
            var lastNameCandidate = (string?)null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in container.DescendantsAndSelf().Where(n => n.NodeType == HtmlNodeType.Element))
            {
                var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // skip very short tokens
                if (text.Length <= 2) continue;

                // if node contains a price then pair with lastNameCandidate or nearby sibling
                var priceMatch = PriceRegex.Match(text);
                if (priceMatch.Success)
                {
                    var price = priceMatch.Value.Trim();

                    // Prefer name extracted from class tokens in nearby siblings/parent
                    var nameFromClass = FindNameByClass(node) ?? FindNameByClass(node.ParentNode) ?? lastNameCandidate;

                    // If still missing, look backwards in sibling chain for a good text fragment
                    if (string.IsNullOrWhiteSpace(nameFromClass))
                    {
                        var prev = node.PreviousSibling;
                        while (prev != null)
                        {
                            var ptxt = HtmlEntity.DeEntitize(prev.InnerText ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(ptxt) && !PriceRegex.IsMatch(ptxt) && ptxt.Length > 3)
                            {
                                nameFromClass = ptxt;
                                break;
                            }
                            prev = prev.PreviousSibling;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(nameFromClass))
                    {
                        // as a last resort, look among recent ancestors' significant text nodes
                        var ancestorText = node.AncestorsAndSelf()
                            .SelectMany(a => a.ChildNodes.Where(c => c.NodeType == HtmlNodeType.Element))
                            .Select(c => HtmlEntity.DeEntitize(c.InnerText ?? string.Empty).Trim())
                            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && !PriceRegex.IsMatch(t) && t.Length > 3);
                        nameFromClass = ancestorText;
                    }

                    if (string.IsNullOrWhiteSpace(nameFromClass)) continue;

                    var key = (nameFromClass + "|" + price).ToLowerInvariant();
                    if (seen.Contains(key)) continue;
                    seen.Add(key);

                    items.Add(new MenuItem { Name = nameFromClass.Trim(), Price = price });
                    lastNameCandidate = null; // consume
                    continue;
                }

                // not a price: decide if this fragment is a name candidate
                if (!PriceRegex.IsMatch(text) && text.Length > 3 && !IsLikelySectionHeading(text))
                {
                    // prefer nodes with name-like classes/tags
                    if (HasNameClass(node) || node.Name.StartsWith("h") || node.Name == "strong" || node.Name == "b" || node.Name == "p" || node.Name == "span")
                    {
                        lastNameCandidate = text;
                    }
                    else
                    {
                        // keep it as candidate if it's sufficiently long
                        lastNameCandidate = text.Length > (lastNameCandidate?.Length ?? 0) ? text : lastNameCandidate;
                    }
                }
            }

            return items.Count > 0 ? items : null;
        }

        // Helper: find a name by class/id tokens inside node
        private string? FindNameByClass(HtmlNode node)
        {
            if (node == null) return null;
            var n = node.DescendantsAndSelf().FirstOrDefault(d =>
            {
                var cls = d.GetAttributeValue("class", "") + " " + d.GetAttributeValue("id", "");
                return NameClassTokens.Any(t => cls.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            });
            if (n != null)
            {
                var txt = HtmlEntity.DeEntitize(n.InnerText ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(txt) && !PriceRegex.IsMatch(txt)) return txt;
            }
            return null;
        }

        // Helper: find price by class/id tokens inside node
        private string? FindPriceByClass(HtmlNode node)
        {
            if (node == null) return null;
            var n = node.DescendantsAndSelf().FirstOrDefault(d =>
            {
                var cls = d.GetAttributeValue("class", "") + " " + d.GetAttributeValue("id", "");
                return PriceClassTokens.Any(t => cls.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            });
            if (n != null)
            {
                var m = PriceRegex.Match(HtmlEntity.DeEntitize(n.InnerText ?? string.Empty));
                if (m.Success) return m.Value;
            }
            return null;
        }

        // Search for any price substring inside node text
        private string? FindPriceInNode(HtmlNode node)
        {
            if (node == null) return null;
            var txt = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty);
            var m = PriceRegex.Match(txt);
            return m.Success ? m.Value : null;
        }

        // Choose best textual candidate inside node for a name if class heuristics fail
        private string? FindBestTextCandidate(HtmlNode node)
        {
            if (node == null) return null;
            // prefer headings or bold elements
            var candidate = node.DescendantsAndSelf()
                .Where(d => d.NodeType == HtmlNodeType.Element)
                .OrderByDescending(d => (d.Name.StartsWith("h") ? 10 : 0) + (HasNameClass(d) ? 8 : 0) + (d.InnerText?.Length ?? 0))
                .Select(d => HtmlEntity.DeEntitize(d.InnerText ?? string.Empty).Trim())
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && !PriceRegex.IsMatch(t) && !IsLikelySectionHeading(t));
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

            // fallback: split by lines and pick the longest non-price line
            var lines = SplitToLines(HtmlEntity.DeEntitize(node.InnerText ?? string.Empty));
            return lines.OrderByDescending(l => l.Length).FirstOrDefault(l => !PriceRegex.IsMatch(l) && l.Length > 3);
        }

        private static List<string> SplitToLines(string text)
        {
            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
        }

        private static bool HasNameClass(HtmlNode n)
        {
            if (n == null) return false;
            var cls = (n.GetAttributeValue("class", "") + " " + n.GetAttributeValue("id", "")).ToLowerInvariant();
            return NameClassTokens.Any(tok => cls.Contains(tok));
        }

        private static bool IsLikelySectionHeading(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.ToLowerInvariant();
            return MenuKeywords.Any(k => t.Contains(k)) || t.Length <= 2;
        }

        private string? GetContainerSectionName(HtmlNode container)
        {
            if (container == null) return null;
            // look for aria-label or data attributes, or class tokens
            var aria = container.GetAttributeValue("aria-label", null);
            if (!string.IsNullOrWhiteSpace(aria)) return aria;

            var cls = container.GetAttributeValue("class", null);
            if (!string.IsNullOrWhiteSpace(cls))
            {
                foreach (var token in SectionClassTokens)
                {
                    if (cls.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // pick the class token as a simple section name
                        return token;
                    }
                }
            }

            // look for closest preceding heading element
            var heading = container.SelectSingleNode("preceding::h1|preceding::h2|preceding::h3|preceding::h4");
            if (heading != null) return HtmlEntity.DeEntitize(heading.InnerText)?.Trim();
            return null;
        }

        // Fallback heuristic: look for elements containing price text and pair with sibling/ancestor text nodes
        private MenuExtractionResult ExtractByPriceLayout(string html, string sourceUrl)
        {
            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var priceNodes = doc.DocumentNode
                    .SelectNodes("//*[text()]")
                    ?.Where(n => PriceRegex.IsMatch(HtmlEntity.DeEntitize(n.InnerText ?? string.Empty)))
                    .ToList() ?? new List<HtmlNode>();

                var items = new List<MenuItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var priceNode in priceNodes)
                {
                    // find a nearby container which likely groups name and price
                    var container = priceNode;
                    for (int i = 0; i < 4 && container != null; i++)
                    {
                        if (container.ParentNode != null) container = container.ParentNode;
                    }

                    if (container == null) continue;

                    // collect candidate text fragments inside container
                    var fragments = container.DescendantsAndSelf()
                        .Where(n => n.NodeType == HtmlNodeType.Element)
                        .Select(n => HtmlEntity.DeEntitize(n.InnerText ?? string.Empty).Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct()
                        .ToList();

                    if (fragments.Count == 0) continue;

                    // find price string from fragments (prefer the exact price node)
                    var priceMatch = fragments.FirstOrDefault(f => PriceRegex.IsMatch(f)) ?? PriceRegex.Match(priceNode.InnerText ?? string.Empty).Value;
                    if (string.IsNullOrWhiteSpace(priceMatch)) continue;

                    // find best candidate name: prefer a sibling or earlier fragment that is not price
                    string? nameCandidate = null;

                    // 1) try node siblings in same container
                    var siblingTexts = priceNode.ParentNode?.ChildNodes
                        .Where(n => n.NodeType == HtmlNodeType.Element)
                        .Select(n => HtmlEntity.DeEntitize(n.InnerText ?? string.Empty).Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();

                    if (siblingTexts != null && siblingTexts.Count > 0)
                    {
                        // pick nearest previous sibling text that is not a price
                        var prev = priceNode.PreviousSibling;
                        while (prev != null)
                        {
                            var ptxt = HtmlEntity.DeEntitize(prev.InnerText ?? string.Empty).Trim();
                            if (!string.IsNullOrWhiteSpace(ptxt) && !PriceRegex.IsMatch(ptxt) && ptxt.Length > 2)
                            {
                                nameCandidate = ptxt;
                                break;
                            }
                            prev = prev.PreviousSibling;
                        }
                    }

                    // 2) if still null, pick longest non-price fragment in container
                    if (string.IsNullOrWhiteSpace(nameCandidate))
                    {
                        nameCandidate = fragments.Where(f => !PriceRegex.IsMatch(f) && f.Length > 2).OrderByDescending(f => f.Length).FirstOrDefault();
                    }

                    if (string.IsNullOrWhiteSpace(nameCandidate)) continue;

                    var key = (nameCandidate + "|" + priceMatch).ToLowerInvariant();
                    if (seen.Contains(key)) continue;
                    seen.Add(key);

                    items.Add(new MenuItem { Name = nameCandidate, Price = priceMatch });
                }

                if (items.Any())
                {
                    var structured = new StructuredMenu { SourceUrl = sourceUrl };
                    structured.Sections.Add(new MenuSection { Name = "Detected Menu (price-pair heuristic)", Items = items });
                    var raw = string.Join("\n", items.Select(i => $"{i.Name} {i.Price}"));
                    return new MenuExtractionResult { Found = true, SourceUrl = sourceUrl, RawText = raw, StructuredMenu = structured };
                }
            }
            catch
            {
                // ignore errors in heuristic
            }

            return new MenuExtractionResult { Found = false };
        }

        // Attempt to download and parse a PDF menu (best-effort)
        private async Task<MenuExtractionResult> TryExtractFromPdfAsync(string pdfUrl)
        {
            try
            {
                using var resp = await _http.GetAsync(pdfUrl);
                if (!resp.IsSuccessStatusCode) return new MenuExtractionResult { Found = false };

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > _maxPageBytes) return new MenuExtractionResult { Found = false };

                using var ms = new MemoryStream(bytes);
                using var doc = PdfDocument.Open(ms);
                var sb = new StringBuilder();
                foreach (var page in doc.GetPages())
                {
                    sb.AppendLine(page.Text);
                }
                var text = sb.ToString();

                var items = ExtractMenuItemsFromText(text);
                if (items.Any())
                {
                    var structured = new StructuredMenu { SourceUrl = pdfUrl };
                    structured.Sections.Add(new MenuSection { Name = "PDF Menu", Items = items });
                    return new MenuExtractionResult
                    {
                        Found = true,
                        SourceUrl = pdfUrl,
                        RawText = text.Length > 2000 ? text.Substring(0, 2000) : text,
                        StructuredMenu = structured
                    };
                }

                return new MenuExtractionResult { Found = false };
            }
            catch
            {
                return new MenuExtractionResult { Found = false };
            }
        }

        #region JSON-LD parsing helpers

        // Walk JSON-LD and try to construct a StructuredMenu
        private StructuredMenu? ExtractStructuredMenuFromJsonElement(JsonElement root, string sourceUrl)
        {
            var foundSections = new List<MenuSection>();
            var foundItems = new List<MenuItem>();

            void Walk(JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.Object)
                {
                    var typeName = GetTypeName(el);
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        if (typeName.IndexOf("menuitem", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var mi = ParseMenuItem(el);
                            if (mi != null) foundItems.Add(mi);
                        }
                        else if (typeName.IndexOf("menusection", StringComparison.OrdinalIgnoreCase) >= 0 || typeName.Equals("menu", StringComparison.OrdinalIgnoreCase))
                        {
                            var sec = ParseMenuSection(el);
                            if (sec != null) foundSections.Add(sec);
                        }
                        else if (typeName.Equals("restaurant", StringComparison.OrdinalIgnoreCase))
                        {
                            // Restaurant might contain hasMenu
                            if (el.TryGetProperty("hasMenu", out var hasMenu))
                            {
                                Walk(hasMenu);
                            }
                        }
                    }

                    // descend
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array)
                            Walk(prop.Value);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray()) Walk(item);
                }
            }

            Walk(root);

            var structured = new StructuredMenu { SourceUrl = sourceUrl };
            if (foundSections.Any())
            {
                structured.Sections.AddRange(foundSections);
            }
            else if (foundItems.Any())
            {
                structured.Sections.Add(new MenuSection { Name = "Menu", Items = foundItems });
            }

            return structured.Sections.Any() ? structured : null;
        }

        private static string GetTypeName(JsonElement el)
        {
            if (el.TryGetProperty("@type", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString() ?? string.Empty;
            if (el.TryGetProperty("type", out var t2) && t2.ValueKind == JsonValueKind.String)
                return t2.GetString() ?? string.Empty;
            return string.Empty;
        }

        private static MenuItem? ParseMenuItem(JsonElement el)
        {
            try
            {
                string? name = null;
                string? description = null;
                string? price = null;

                if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                if (el.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString();

                // offers may contain price
                if (el.TryGetProperty("offers", out var offers))
                {
                    if (offers.ValueKind == JsonValueKind.Object)
                    {
                        if (offers.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.String) price = pr.GetString();
                        else if (offers.TryGetProperty("priceSpecification", out var ps) && ps.ValueKind == JsonValueKind.Object && ps.TryGetProperty("price", out var p2) && p2.ValueKind == JsonValueKind.String) price = p2.GetString();
                    }
                    else if (offers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var o in offers.EnumerateArray())
                        {
                            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("price", out var pr2) && pr2.ValueKind == JsonValueKind.String)
                            {
                                price = pr2.GetString();
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(name) && el.TryGetProperty("headline", out var h) && h.ValueKind == JsonValueKind.String)
                    name = h.GetString();

                return new MenuItem { Name = name, Description = description, Price = price };
            }
            catch
            {
                return null;
            }
        }

        private static MenuSection? ParseMenuSection(JsonElement el)
        {
            try
            {
                var section = new MenuSection();
                if (el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) section.Name = n.GetString();

                // find items in hasMenuItem / hasMenuItems / hasMenu
                var items = new List<MenuItem>();
                if (el.TryGetProperty("hasMenuItem", out var hmi)) CollectMenuItems(hmi, items);
                if (el.TryGetProperty("hasMenu", out var hm)) CollectMenuItems(hm, items);
                if (el.TryGetProperty("hasMenuSection", out var hms))
                {
                    // nested sections -> treat nested items as part of parent section
                    if (hms.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in hms.EnumerateArray())
                        {
                            if (s.ValueKind == JsonValueKind.Object)
                            {
                                var nested = ParseMenuSection(s);
                                if (nested != null) items.AddRange(nested.Items);
                            }
                        }
                    }
                }

                section.Items = items;
                return section.Items.Any() ? section : null;
            }
            catch
            {
                return null;
            }
        }

        private static void CollectMenuItems(JsonElement el, List<MenuItem> items)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                var mi = ParseMenuItem(el);
                if (mi != null) items.Add(mi);
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in el.EnumerateArray())
                {
                    if (it.ValueKind == JsonValueKind.Object)
                    {
                        var mi = ParseMenuItem(it);
                        if (mi != null) items.Add(mi);
                    }
                }
            }
        }

        #endregion

        #region Utilities (existing heuristics retained)

        private string? GuessSectionName(HtmlNode node)
        {
            var h = node.SelectSingleNode("preceding::h1|preceding::h2|preceding::h3|preceding::h4");
            if (h != null) return HtmlEntity.DeEntitize(h.InnerText).Trim();
            var parent = node.ParentNode;
            if (parent != null)
            {
                var cls = parent.GetAttributeValue("class", null);
                if (!string.IsNullOrEmpty(cls)) return cls;
            }
            return null;
        }

        private static string MakeAbsoluteUrl(string baseUrl, string href)
        {
            try
            {
                if (Uri.IsWellFormedUriString(href, UriKind.Absolute)) return href;
                var baseUri = new Uri(baseUrl);
                return new Uri(baseUri, href).ToString();
            }
            catch { return href; }
        }

        private async Task<string?> FetchHtmlAsync(string url)
        {
            try
            {
                using var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                var bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0 || bytes.Length > _maxPageBytes) return null;

                var charset = resp.Content.Headers.ContentType?.CharSet;
                var html = !string.IsNullOrEmpty(charset)
                    ? Encoding.GetEncoding(charset).GetString(bytes)
                    : Encoding.UTF8.GetString(bytes);

                return html;
            }
            catch
            {
                return null;
            }
        }

        private static string GetPlainText(HtmlNode node) => HtmlEntity.DeEntitize(node.InnerText ?? string.Empty).Trim();

        private static string ExtractItemName(string line)
        {
            var idx = line.IndexOf(" - ");
            if (idx < 0) idx = line.IndexOf(" — ");
            if (idx < 0) idx = line.IndexOf("–");
            if (idx > 0) return line.Substring(0, idx).Trim();
            var m = PriceRegex.Match(line);
            if (m.Success)
            {
                var before = line.Substring(0, m.Index).Trim();
                if (!string.IsNullOrWhiteSpace(before)) return before;
            }
            return line.Length <= 60 ? line : line.Substring(0, 60).Trim();
        }

        private static string? ExtractPrice(string line)
        {
            var m = PriceRegex.Match(line);
            return m.Success ? m.Value.Trim() : null;
        }

        private static string? ExtractDescription(string line)
        {
            var parts = line.Split(new[] { " - ", " — " }, StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                var last = parts.Last();
                if (PriceRegex.IsMatch(last))
                {
                    return string.Join(" - ", parts.Skip(1).Take(parts.Length - 2)).Trim();
                }
                return string.Join(" - ", parts.Skip(1)).Trim();
            }
            return null;
        }

        private static List<MenuItem> ExtractMenuItemsFromText(string text)
        {
            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => HtmlEntity.DeEntitize(l).Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 2).ToList();

            var items = new List<MenuItem>();
            foreach (var line in lines)
            {
                if (MenuKeywords.Any(k => line.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) && line.Length < 40) continue;
                if (PriceRegex.IsMatch(line) || line.Contains("-") || line.Contains("–"))
                {
                    items.Add(new MenuItem { Name = ExtractItemName(line), Description = ExtractDescription(line), Price = ExtractPrice(line) });
                }
            }
            return items;
        }

        #endregion
    }
}