using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace FoodMenu_Extract.Services
{
    // Simple Playwright wrapper to render pages and return the final HTML.
    public sealed class HeadlessRenderer : IAsyncDisposable
    {
        private readonly IPlaywright _playwright;
        private readonly IBrowser _browser;

        private HeadlessRenderer(IPlaywright playwright, IBrowser browser)
        {
            _playwright = playwright;
            _browser = browser;
        }

        public static async Task<HeadlessRenderer> CreateAsync(bool headless = true, int launchTimeoutMs = 60000)
        {
            var playwright = await Playwright.CreateAsync();
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Timeout = launchTimeoutMs
            });
            return new HeadlessRenderer(playwright, browser);
        }

        public async Task<string?> RenderPageAsync(string url, int timeoutMs = 30000)
        {
            try
            {
                // create a short-lived context to reduce memory/leaks
                var context = await _browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = true });
                var page = await context.NewPageAsync();
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = timeoutMs });
                var content = await page.ContentAsync();
                await context.CloseAsync();
                return content;
            }
            catch
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { if (_browser != null) await _browser.CloseAsync(); } catch { }
            try { _playwright?.Dispose(); } catch { }
        }
    }
}
