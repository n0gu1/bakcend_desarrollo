using System;
using System.Threading;
using System.Threading.Tasks;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace BaseUsuarios.Api.Services.PdfHtml
{
    public interface IHtmlPdfService
    {
        byte[] FromHtml(string html);
    }

    /// <summary>
    /// Renderiza HTML a PDF con Chromium (PuppeteerSharp).
    /// </summary>
    public sealed class ChromiumHtmlPdfService : IHtmlPdfService, IAsyncDisposable
    {
        private Task<IBrowser>? _browserTask;
        private readonly SemaphoreSlim _initLock = new(1, 1);

        private async Task<IBrowser> GetBrowserAsync()
        {
            if (_browserTask != null) return await _browserTask;

            await _initLock.WaitAsync();
            try
            {
                if (_browserTask == null)
                {
                    // Descarga Chromium la primera vez
                    var fetcher = new BrowserFetcher();
                    await fetcher.DownloadAsync();

                    _browserTask = Puppeteer.LaunchAsync(new LaunchOptions
                    {
                        Headless = true,
                        Args = new[]
                        {
                            "--no-sandbox",
                            "--disable-setuid-sandbox",
                            "--disable-gpu",
                            "--font-render-hinting=medium"
                        }
                    });
                }
            }
            finally { _initLock.Release(); }

            return await _browserTask;
        }

        public byte[] FromHtml(string html)
            => FromHtmlAsync(html).GetAwaiter().GetResult();

        private async Task<byte[]> FromHtmlAsync(string html)
        {
            var browser = await GetBrowserAsync();
            await using var page = await browser.NewPageAsync();

            // Carga el HTML en memoria
            await page.SetContentAsync(html, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.Networkidle0 }
            });

            var pdf = await page.PdfDataAsync(new PdfOptions
            {
                Format = PaperFormat.A4,
                PrintBackground = true,
                MarginOptions = new MarginOptions
                {
                    Top = "10mm", Bottom = "10mm", Left = "10mm", Right = "10mm"
                }
            });

            return pdf;
        }

        public async ValueTask DisposeAsync()
        {
            if (_browserTask != null)
            {
                try { await (await _browserTask).CloseAsync(); } catch { /* ignore */ }
            }
            _initLock.Dispose();
        }
    }
}
