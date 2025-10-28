using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FoodMenu_Extract.Models;
using FoodMenu_Extract.Services;
using Microsoft.Extensions.Configuration;

internal static class Program
{
    private static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var connString = config.GetConnectionString("HotelDatabase")
                         ?? throw new InvalidOperationException("Missing connection string 'HotelDatabase'.");

        var gcsSection = config.GetSection("GoogleCustomSearch");
        var apiKey = gcsSection.GetValue<string>("ApiKey") ?? throw new InvalidOperationException("Missing Google API key.");
        var cx = gcsSection.GetValue<string>("SearchEngineId") ?? throw new InvalidOperationException("Missing SearchEngineId.");
        var numResults = gcsSection.GetValue<int?>("NumResults") ?? 5;
        var enablePageCrawl = gcsSection.GetValue<bool?>("EnablePageCrawl") ?? true;
        var maxPageBytes = gcsSection.GetValue<int?>("MaxPageBytes") ?? 2_000_000;

        var batchSize = config.GetSection("DataRepository").GetValue<int?>("BatchSize") ?? 100;
        var maxHotelsToProcess = config.GetSection("DataRepository").GetValue<int?>("MaxHotelsToProcess") ?? 2;

        // New scraper settings
        var scraperSection = config.GetSection("Scraper");
        var enableHeadless = scraperSection.GetValue<bool?>("EnableHeadlessRendering") ?? false;
        var headlessMode = scraperSection.GetValue<bool?>("HeadlessMode") ?? true;

        using var httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(30) };

        // Create optional headless renderer
        HeadlessRenderer? renderer = null;
        if (enableHeadless)
        {
            Console.WriteLine("Initializing headless renderer (Playwright) — ensure browsers are installed (run 'playwright install' once).");
            renderer = await HeadlessRenderer.CreateAsync(headless: headlessMode);
        }

        var searchClient = new GoogleSearchClient(httpClient, apiKey, cx, numResults);
        var extractor = new MenuExtractor(httpClient, enablePageCrawl, maxPageBytes, renderer);
        var repo = new DataRepository(connString, batchSize);

        await foreach (var hotel in repo.ReadHotelsAsync(maxHotelsToProcess))
        {
            try
            {
                Console.WriteLine($"Processing: {hotel.HotelName} ({hotel.ProductId})");

                // Get prioritized candidates (brand pages and paths containing 'dining'/'menu' bubble to the top)
                var candidates = await searchClient.FindBestMenuCandidatesAsync(hotel.HotelName, numResults);

                if (candidates == null || candidates.Count == 0)
                {
                    candidates = await searchClient.SearchAsync(hotel.HotelName + " menu");
                }

                MenuResultDto result = new MenuResultDto
                {
                    HotelProductId = hotel.ProductId,
                    HotelName = hotel.HotelName,
                    HotelAddress = hotel.Address,
                    HasMenu = false
                };

                foreach (var candidate in candidates)
                {
                    Console.WriteLine($"  Trying candidate: {candidate.Link} (host: {candidate.Host})");
                    var found = await extractor.TryExtractMenuFromCandidateAsync(candidate.Link);
                    if (!found.Found)
                    {
                        Console.WriteLine($"    No menu found at {candidate.Link}");
                        continue;
                    }

                    result.HasMenu = true;
                    result.MenuSourceUrl = found.SourceUrl;
                    result.MenuText = found.RawText;
                    result.MenuJson = JsonSerializer.Serialize(found.StructuredMenu, new JsonSerializerOptions { WriteIndented = false });

                    Console.WriteLine($"    Menu found at {found.SourceUrl}");
                    break;
                }

                // Broad fallback if nothing found on top candidates
                if (!result.HasMenu)
                {
                    Console.WriteLine("  No menu found on top candidates — trying broader search.");
                    var broad = await searchClient.SearchAsync(hotel.HotelName + " menu");
                    foreach (var candidate in broad)
                    {
                        Console.WriteLine($"  Trying broader candidate: {candidate.Link}");
                        var found = await extractor.TryExtractMenuFromCandidateAsync(candidate.Link);
                        if (!found.Found) continue;

                        result.HasMenu = true;
                        result.MenuSourceUrl = found.SourceUrl;
                        result.MenuText = found.RawText;
                        result.MenuJson = JsonSerializer.Serialize(found.StructuredMenu, new JsonSerializerOptions { WriteIndented = false });
                        Console.WriteLine($"    Menu found at {found.SourceUrl}");
                        break;
                    }
                }

                await repo.UpsertMenuResultAsync(result);
                Console.WriteLine($"Saved: {hotel.HotelName} -> HasMenu={result.HasMenu} Source={result.MenuSourceUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {hotel.HotelName}: {ex.Message}");
            }
        }

        // dispose renderer if created
        if (renderer != null) await renderer.DisposeAsync();

        Console.WriteLine("Completed.");
    }
}