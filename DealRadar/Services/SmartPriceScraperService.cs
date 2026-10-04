using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace DealRadar.Api.Services;

public record ScrapeResult(
    bool IsSuccess,
    decimal? ListedPrice,
    decimal CouponDiscount,
    decimal MaxCardDiscount,
    decimal? EffectivePrice,
    List<ParsedCardOffer> CardOffers,
    bool IsInStock,
    string? ErrorMessage = null);

public class SmartPriceScraperService : IAsyncDisposable
{
    private readonly ILogger<SmartPriceScraperService> _logger;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    public SmartPriceScraperService(ILogger<SmartPriceScraperService> logger)
    {
        _logger = logger;
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser != null && _browser.IsConnected)
            return _browser;

        await _browserLock.WaitAsync();
        try
        {
            if (_browser == null || !_browser.IsConnected)
            {
                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true, // Set to false while debugging if you want to see the browser window
                    Args =
                    [
                        "--disable-blink-features=AutomationControlled",
                        "--disable-features=IsolateOrigins,site-per-process",
                        "--no-sandbox",
                        "--disable-dev-shm-usage"
                    ]
                });
            }
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async Task<ScrapeResult> FetchPriceAsync(string websiteName, string rawUrl)
    {
        var browser = await GetBrowserAsync();
        string key = NormalizeWebsiteKey(websiteName, rawUrl);
        string cleanUrl = CleanTrackingUrl(key, rawUrl);

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            Locale = "en-IN",
            TimezoneId = "Asia/Kolkata",
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            Geolocation = new Geolocation { Latitude = 23.0225f, Longitude = 72.5714f },
            Permissions = ["geolocation"],
            IgnoreHTTPSErrors = true,
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "en-IN,en-GB;q=0.9,en-US;q=0.8,en;q=0.7",
                ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8",
                ["Sec-Ch-Ua"] = "\"Google Chrome\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"",
                ["Sec-Ch-Ua-Mobile"] = "?0",
                ["Sec-Ch-Ua-Platform"] = "\"Windows\"",
                ["Sec-Fetch-Dest"] = "document",
                ["Sec-Fetch-Mode"] = "navigate",
                ["Sec-Fetch-Site"] = "none",
                ["Sec-Fetch-User"] = "?1",
                ["Upgrade-Insecure-Requests"] = "1"
            }
        });

        var page = await context.NewPageAsync();

        // Full stealth script to mask headless browser fingerprint
        await page.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.chrome = { runtime: {} };
            Object.defineProperty(navigator, 'languages', { get: () => ['en-IN', 'en-US', 'en'] });
            Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
        ");

        // Block only heavy video/audio/font files; allow images/scripts/XHR so SPAs never break
        await page.RouteAsync("**/*", async route =>
        {
            var type = route.Request.ResourceType;
            if (type is "media" or "font")
                await route.AbortAsync();
            else
                await route.ContinueAsync();
        });

        // Capture background JSON API responses (catches dynamic prices on Croma, Reliance, Swiggy, JioMart, Flipkart)
        var interceptedApiPrices = new ConcurrentBag<decimal>();
        page.Response += async (_, response) =>
        {
            try
            {
                if (response.Status == 200 &&
                    (response.Request.ResourceType is "xhr" or "fetch") &&
                    (response.Headers.TryGetValue("content-type", out var ct) && ct.Contains("json")))
                {
                    var json = await response.TextAsync();
                    if (json.Length > 20 && json.Length < 500_000)
                    {
                        var priceFromApi = TryExtractPriceFromRawJson(json);
                        if (priceFromApi is > 50)
                            interceptedApiPrices.Add(priceFromApi.Value);
                    }
                }
            }
            catch { /* Ignore background response read errors */ }
        };

        try
        {
            return await ScrapeUniversalPipelineAsync(page, key, cleanUrl, interceptedApiPrices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scraping {Website} ({Url})", websiteName, cleanUrl);
            return new ScrapeResult(false, null, 0, 0, null, [], false, ex.Message);
        }
    }

    // =========================================================================
    // MASTER 6-LAYER SCRAPING PIPELINE (WORKS FOR ALL 10 WEBSITES)
    // =========================================================================
    private async Task<ScrapeResult> ScrapeUniversalPipelineAsync(
        IPage page,
        string storeKey,
        string url,
        ConcurrentBag<decimal> interceptedApiPrices)
    {
        // 1. Navigate safely
        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });
        }
        catch (TimeoutException)
        {
            // Even if DOMContentLoaded times out on slow sites, continue and inspect what loaded
            _logger.LogWarning("Navigation timeout on {Store}, attempting extraction from partial DOM.", storeKey);
        }

        // Wait for React / Next.js / Angular hydration
        int initialWaitMs = storeKey is "swiggyinstamart" or "reliancedigital" or "croma" or "tataneu" ? 3500 : 2500;
        await page.WaitForTimeoutAsync(initialWaitMs);

        // 2. Dismiss Login / Location Popups (Flipkart, Myntra, etc.)
        await DismissPopupsAsync(page);

        // Scroll slightly to trigger lazy-rendered price & bank offer widgets
        await page.EvaluateAsync("window.scrollBy(0, 300)");
        await page.WaitForTimeoutAsync(800);

        // 3. Check for Amazon CAPTCHA
        if (storeKey == "amazon" && await page.Locator("input#captchacharacters").CountAsync() > 0)
            return new ScrapeResult(false, null, 0, 0, null, [], false, "Blocked by Amazon CAPTCHA");

        var pageTitle = await page.TitleAsync();
        var bodyText = await page.Locator("body").InnerTextAsync();

        // 4. Check Out-of-Stock Status
        bool isOutOfStock = CheckIfOutOfStock(storeKey, bodyText);

        // =====================================================================
        // PRICE EXTRACTION WATERFALL (6 LAYERS)
        // =====================================================================
        decimal? listedPrice = null;

        // LAYER 1: Store-Specific Primary CSS Selectors
        var selectors = GetStoreSelectors(storeKey);
        foreach (var sel in selectors)
        {
            var loc = page.Locator(sel).First;
            if (await loc.CountAsync() > 0)
            {
                var attrVal = await loc.GetAttributeAsync("data-price-amount") ?? await loc.GetAttributeAsync("content");
                listedPrice = ParsePrice(attrVal) ?? ParsePrice(await GetInnerTextSafeAsync(page, sel));
                if (listedPrice is > 0) break;
            }
        }

        // LAYER 2: JSON-LD SEO Schema (<script type="application/ld+json">)
        if (listedPrice is null or <= 0)
        {
            listedPrice = await ExtractPriceFromJsonLdAsync(page);
        }

        // LAYER 3: OpenGraph & Product Meta Tags (<meta property="product:price:amount" content="...">)
        if (listedPrice is null or <= 0)
        {
            listedPrice = await ExtractPriceFromMetaTagsAsync(page);
        }

        // LAYER 4: Embedded Window JS State (__INITIAL_STATE__, __NEXT_DATA__, __myx) + Visual Font-Size Scanner
        if (listedPrice is null or <= 0)
        {
            listedPrice = await ExtractFromJsStateAndVisualDomAsync(page);
        }

        // LAYER 5: Visible Body Text Regex Patterns ("Special price ₹12,999", "Deal Price: ₹...", "₹12,999 ₹15,999 18% off")
        if (listedPrice is null or <= 0)
        {
            listedPrice = ExtractPriceFromBodyTextRegex(bodyText);
        }

        // LAYER 6: Intercepted XHR/Fetch Network JSON Responses
        if (listedPrice is null or <= 0 && !interceptedApiPrices.IsEmpty)
        {
            listedPrice = interceptedApiPrices.FirstOrDefault(p => p > 0);
        }

        if (listedPrice is null or <= 0)
        {
            return new ScrapeResult(
                IsSuccess: false,
                ListedPrice: null,
                CouponDiscount: 0,
                MaxCardDiscount: 0,
                EffectivePrice: null,
                CardOffers: [],
                IsInStock: !isOutOfStock,
                ErrorMessage: $"Price not found on {storeKey} (Page Title: {pageTitle})");
        }

        // =====================================================================
        // COUPON & BANK CARD OFFER EXTRACTION
        // =====================================================================
        decimal couponDiscount = 0;
        if (storeKey == "amazon")
        {
            var couponText = await GetInnerTextSafeAsync(page, "#promoPriceBlockMessage_feature_div, .promoPriceBlockMessage");
            couponDiscount = BankOfferParser.ParseCouponDiscount(couponText, listedPrice.Value);
        }

        // Expand hidden bank offers on Amazon / Flipkart before scanning
        await ExpandBankOfferDrawersAsync(page, storeKey);

        var rawBankTexts = await ExtractOfferLinesViaJsAsync(page);
        var finalResult = BuildFinalResult(listedPrice.Value, couponDiscount, rawBankTexts, storeKey);

        return finalResult with { IsInStock = !isOutOfStock };
    }

    // =========================================================================
    // STORE SELECTOR MAP (ALL 10 STORES)
    // =========================================================================
    private static string[] GetStoreSelectors(string storeKey) => storeKey switch
    {
        "amazon" =>
        [
            // Standard Buy Box
            "#corePriceDisplay_desktop_feature_div .priceToPay .a-price-whole",
            "#corePriceDisplay_desktop_feature_div .a-price-whole",
            "#corePrice_feature_div .a-price .a-offscreen",
            "#apex_desktop .a-price .a-offscreen",
    
            // Fallback: "See All Buying Options" / Non-standard Seller Box
            "#apex_desktop .a-price-whole",
            "#olpLinkWidget_feature_div .a-color-price",
            "#buybox-see-all-buying-choices .a-color-price",
            "#aod-price-1 .a-price-whole",
            "span[data-a-size='xl'] .a-price-whole",
            ".priceToPay span.a-price-whole"
        ],
        "flipkart" =>
        [
            "div.Nx9bqj.CxhGGd",
            "div._30jeq3._16Jk6d",
            "div.hl05eU div.Nx9bqj",
            "div.CEmiEU div.Nx9bqj"
        ],
        "reliancedigital" =>
        [
            ".pdp__offerPrice",
            "li.pdp__priceSection__priceListText span",
            "span[class*='offerPrice']",
            "div[class*='offerPrice']",
            ".pdp-price"
        ],
        "myntra" =>
        [
            ".pdp-price strong",
            "span.pdp-price",
            ".pdp-discount-container .pdp-price"
        ],
        "bigbasket" =>
        [
            "td.Description___StyledTd-sc-82a36a-4",
            "[class*='Price___StyledLabel']",
            "td:has-text('Price:')"
        ],
        "swiggyinstamart" =>
        [
            "[data-testid='item-offer-price']",
            "[data-testid='item-price']",
            "div[class*='OfferPrice']",
            "div[class*='item-price']"
        ],
        "croma" =>
        [
            "#pdp-product-price",
            ".cp-price.main-product-price .amount",
            ".new-price .amount",
            "[data-testid='new-price']"
        ],
        "vijaysales" =>
        [
            ".price-wrapper [data-price-amount]",
            "#product-price .price",
            ".vs-price",
            ".special-price .price",
            ".pdp-price-info .price"
        ],
        "jiomart" =>
        [
            "#price_section .jm-heading-xs",
            ".product-price .jm-heading-xs",
            "span.jm-heading-xs",
            "#pdp_product_price"
        ],
        "tataneu" =>
        [
            "[class*='ProductDetailsMainCard'] h3",
            "[class*='Price'] h3",
            "[data-testid='product-price']",
            ".pdp-price"
        ],
        _ => []
    };

    // =========================================================================
    // URL SANITIZER (STRIPS GOOGLE ADS / GCLID / AFFILIATE PARAMS)
    // =========================================================================
    private static string CleanTrackingUrl(string storeKey, string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return rawUrl;
        rawUrl = rawUrl.Trim();

        if (storeKey == "amazon")
        {
            var asinMatch = Regex.Match(rawUrl, @"/(?:dp|gp/product)/([A-Z0-9]{10})", RegexOptions.IgnoreCase);
            if (asinMatch.Success)
                return $"https://www.amazon.in/dp/{asinMatch.Groups[1].Value}";
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
            return rawUrl;

        var basePath = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

        if (storeKey == "flipkart")
        {
            // Keep ONLY ?pid=XXXX on Flipkart; strip gclid, cmpid, lid, marketplace, etc.
            var pidMatch = Regex.Match(uri.Query, @"[?&](pid=[A-Z0-9]+)", RegexOptions.IgnoreCase);
            return pidMatch.Success ? $"{basePath}?{pidMatch.Groups[1].Value}" : basePath;
        }

        // For Reliance Digital, Croma, Myntra, VijaySales, JioMart, BigBasket, Swiggy, TataNeu:
        // Strip Google/Facebook ad tracking params if present
        if (uri.Query.Contains("gclid=", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("utm_", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("gad_source=", StringComparison.OrdinalIgnoreCase))
        {
            return basePath;
        }

        return rawUrl;
    }

    // =========================================================================
    // LAYER 2: JSON-LD EXTRACTOR
    // =========================================================================
    private async Task<decimal?> ExtractPriceFromJsonLdAsync(IPage page)
    {
        var scripts = await page.Locator("script[type='application/ld+json']").AllInnerTextsAsync();
        foreach (var json in scripts)
        {
            var match = Regex.Match(json, @"""(?:lowPrice|price)""\s*:\s*""?(\d+(?:\.\d+)?)""?");
            if (match.Success && decimal.TryParse(match.Groups[1].Value, out var p) && p > 0)
                return p;
        }
        return null;
    }

    // =========================================================================
    // LAYER 3: META TAG EXTRACTOR
    // =========================================================================
    private async Task<decimal?> ExtractPriceFromMetaTagsAsync(IPage page)
    {
        string[] metaSelectors =
        [
            "meta[property='product:price:amount']",
            "meta[property='og:price:amount']",
            "meta[name='twitter:data1']",
            "[itemprop='price']"
        ];

        foreach (var sel in metaSelectors)
        {
            var loc = page.Locator(sel).First;
            if (await loc.CountAsync() > 0)
            {
                var content = await loc.GetAttributeAsync("content") ?? await loc.InnerTextAsync();
                var parsed = ParsePrice(content);
                if (parsed is > 0) return parsed;
            }
        }
        return null;
    }

    // =========================================================================
    // LAYER 4: JS WINDOW STATE + VISUAL FONT-SIZE SCANNER
    // =========================================================================
    private async Task<decimal?> ExtractFromJsStateAndVisualDomAsync(IPage page)
    {
        var jsPriceStr = await page.EvaluateAsync<string?>(@"() => {
            // 1. Myntra window.__myx
            try {
                if (window.__myx && window.__myx.pdpData && window.__myx.pdpData.price) {
                    const p = window.__myx.pdpData.price.discounted || window.__myx.pdpData.price.mrp;
                    if (p) return String(p);
                }
            } catch {}

            // 2. Flipkart Redux window.__INITIAL_STATE__
            try {
                if (window.__INITIAL_STATE__) {
                    const s = JSON.stringify(window.__INITIAL_STATE__);
                    const m = s.match(/""(?:finalPrice|sellingPrice|specialPrice)""\s*:\s*\{?[^{}]*""?(?:value|amount|decimalValue)""?\s*:\s*""?(\d+(?:\.\d+)?)""?/i)
                           || s.match(/""(?:finalPrice|sellingPrice)""\s*:\s*(\d+)/i);
                    if (m && parseFloat(m[1]) > 50) return m[1];
                }
            } catch {}

            // 3. Next.js #__NEXT_DATA__ (Reliance Digital, BigBasket, Croma, Swiggy, JioMart)
            try {
                const nextScript = document.querySelector('#__NEXT_DATA__');
                if (nextScript) {
                    const text = nextScript.textContent || '';
                    const m = text.match(/""(?:offerPrice|discountedPrice|selling_price|sellingPrice|sp|finalPrice|dealPrice)""\s*:\s*""?(\d+(?:\.\d+)?)""?/i);
                    if (m && parseFloat(m[1]) > 0) return m[1];
                }
            } catch {}

            // 4. Visual DOM Scanner: Find prominent non-strikethrough ₹ price in the upper product area
            const candidates = [];
            const elements = document.querySelectorAll('h1, h2, h3, h4, span, div, strong, b, td, p');
            const rupeeRegex = /^(?:₹|Rs\.?|INR)\s*([\d,]+(?:\.\d{1,2})?)$/i;

            for (const el of elements) {
                if (el.children.length > 1) continue;
                const raw = (el.innerText || '').trim();
                const match = raw.match(rupeeRegex);
                if (!match) continue;

                const numVal = parseFloat(match[1].replace(/,/g, ''));
                if (!numVal || numVal < 20) continue;

                const style = window.getComputedStyle(el);
                const parentStyle = el.parentElement ? window.getComputedStyle(el.parentElement) : null;

                // Ignore crossed-out MRP prices
                if (style.textDecorationLine.includes('line-through') ||
                    (parentStyle && parentStyle.textDecorationLine.includes('line-through'))) {
                    continue;
                }

                const rect = el.getBoundingClientRect();
                if (rect.top > 70 && rect.top < 850 && rect.width > 0 && rect.height > 0) {
                    const fontSize = parseFloat(style.fontSize) || 14;
                    candidates.push({ price: match[1], fontSize: fontSize, top: rect.top });
                }
            }

            candidates.sort((a, b) => b.fontSize - a.fontSize || a.top - b.top);
            return candidates.length > 0 ? candidates[0].price : null;
        }");

        return ParsePrice(jsPriceStr);
    }

    // =========================================================================
    // LAYER 5: VISIBLE BODY TEXT REGEX FALLBACK
    // =========================================================================
    private static decimal? ExtractPriceFromBodyTextRegex(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return null;

        // Pattern A: "Deal Price: ₹12,999" / "Offer Price: ₹12,999" / "Special price ₹12,999" / "MOP: ₹12,999"
        var labeledMatch = Regex.Match(
            bodyText,
            @"(?:Deal\s*Price|Offer\s*Price|Special\s*Price|Effective\s*Price|Selling\s*Price)\s*[:\-]?\s*(?:₹|Rs\.?)\s*([\d,]+(?:\.\d{1,2})?)",
            RegexOptions.IgnoreCase);

        if (labeledMatch.Success)
        {
            var p = ParsePrice(labeledMatch.Groups[1].Value);
            if (p is > 0) return p;
        }

        // Pattern B: Flipkart/Croma/Reliance pattern -> "₹11,999 ₹17,499 31% off" (SellingPrice followed by MRP and % off)
        var pairMatch = Regex.Match(
            bodyText,
            @"₹\s*([\d,]+)\s+(?:M\.?R\.?P\.?:?\s*)?₹\s*[\d,]+\s+\(?\d{1,2}\s*%\s*(?:off|OFF)\)?");

        if (pairMatch.Success)
        {
            var p = ParsePrice(pairMatch.Groups[1].Value);
            if (p is > 0) return p;
        }

        return null;
    }

    // =========================================================================
    // LAYER 6: RAW XHR/FETCH API JSON PRICE EXTRACTOR
    // =========================================================================
    private static decimal? TryExtractPriceFromRawJson(string json)
    {
        var match = Regex.Match(
            json,
            @"""(?:offerPrice|sellingPrice|finalPrice|discountedPrice|specialPrice|dealPrice|sp)""\s*:\s*""?(\d{2,7}(?:\.\d{1,2})?)""?",
            RegexOptions.IgnoreCase);

        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var price) && price > 0)
            return price;

        return null;
    }

    // =========================================================================
    // POPUP DISMISSER & BANK OFFER EXPANDER
    // =========================================================================
    private static async Task DismissPopupsAsync(IPage page)
    {
        try
        {
            var closeButtons = page.Locator("button._2KpZ6l._2doB4z, span._30XB9F, button[aria-label='Close'], button:has-text('✕')").First;
            if (await closeButtons.CountAsync() > 0 && await closeButtons.IsVisibleAsync())
            {
                await closeButtons.ClickAsync(new LocatorClickOptions { Timeout = 1200 });
                await page.WaitForTimeoutAsync(400);
            }
        }
        catch { }
    }

    private static async Task ExpandBankOfferDrawersAsync(IPage page, string storeKey)
    {
        try
        {
            if (storeKey == "amazon")
            {
                var bankTrigger = page.Locator("#itembox-InstantBankDiscount a, [data-csa-c-item-id='InstantBankDiscount'] a").First;
                if (await bankTrigger.CountAsync() > 0)
                {
                    await bankTrigger.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                    await page.WaitForTimeoutAsync(1200);
                }
            }
            else if (storeKey is "flipkart" or "reliancedigital" or "croma")
            {
                var moreBtn = page.Locator("button:has-text('more offer'), span:has-text('more offer'), a:has-text('View all offers')").First;
                if (await moreBtn.CountAsync() > 0 && await moreBtn.IsVisibleAsync())
                {
                    await moreBtn.ClickAsync(new LocatorClickOptions { Timeout = 1500 });
                    await page.WaitForTimeoutAsync(600);
                }
            }
        }
        catch { }
    }

    private static bool CheckIfOutOfStock(string storeKey, string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return false;

        if (storeKey == "amazon")
            return bodyText.Contains("Currently unavailable.", StringComparison.OrdinalIgnoreCase);

        return (bodyText.Contains("Sold Out", StringComparison.OrdinalIgnoreCase) &&
                bodyText.Contains("out of stock", StringComparison.OrdinalIgnoreCase)) ||
               bodyText.Contains("Currently Unavailable", StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // UNIVERSAL JS DOM OFFER SCANNER
    // =========================================================================
    private static async Task<List<string>> ExtractOfferLinesViaJsAsync(IPage page)
    {
        var lines = await page.EvaluateAsync<string[]>(@"() => {
            const results = new Set();
            const elements = document.querySelectorAll('li, div, span, p');
            const offerRegex = /(Bank\s*Offer|Credit\s*Card|Debit\s*Card|Instant\s*Discount|select\s*Credit\s*Cards|HDFC|ICICI|SBI|Axis|Kotak|Amex|IDFC|BOB|AU\s*Bank|OneCard)/i;
            const currencyRegex = /(₹|Rs\.?|INR)\s*[\d,]+/i;

            for (const el of elements) {
                if (el.children.length > 5) continue;
                const text = (el.innerText || '').replace(/\s+/g, ' ').trim();
                if (text.length >= 15 && text.length <= 280) {
                    if (offerRegex.test(text) && currencyRegex.test(text)) {
                        results.add(text);
                    }
                }
            }
            return Array.from(results);
        }");

        return lines?.ToList() ?? [];
    }

    private static ScrapeResult BuildFinalResult(
    decimal listedPrice,
    decimal couponDiscount,
    IEnumerable<string> rawOfferTexts,
    string websiteName)
    {
        // Filter out Flipkart Axis on Flipkart and Amazon ICICI on Amazon
        var parsedCards = BankOfferParser.ParseOffers(rawOfferTexts, listedPrice, websiteName);
        decimal maxCardDiscount = parsedCards.FirstOrDefault()?.DiscountAmount ?? 0;

        // Effective Buy Price
        decimal effectivePrice = listedPrice - couponDiscount - maxCardDiscount;

        return new ScrapeResult(
            IsSuccess: true,
            ListedPrice: listedPrice,
            CouponDiscount: couponDiscount,
            MaxCardDiscount: maxCardDiscount,
            EffectivePrice: effectivePrice,
            CardOffers: parsedCards,
            IsInStock: true);
    }

    private async Task<string?> GetInnerTextSafeAsync(IPage page, string selector)
    {
        try
        {
            var loc = page.Locator(selector).First;
            return await loc.CountAsync() > 0 ? await loc.InnerTextAsync() : null;
        }
        catch
        {
            return null;
        }
    }

    private static decimal? ParsePrice(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var match = Regex.Match(raw, @"[\d,]+(?:\.\d{1,2})?");
        if (!match.Success) return null;
        var cleaned = match.Value.Replace(",", "");
        return decimal.TryParse(cleaned, out var price) && price > 0 ? price : null;
    }

    public static string NormalizeWebsiteKey(string? websiteName, string url)
    {
        var combined = $"{websiteName} {url}".ToLowerInvariant();
        if (combined.Contains("amazon") || combined.Contains("amzn")) return "amazon";
        if (combined.Contains("flipkart") || combined.Contains("fkrt")) return "flipkart";
        if (combined.Contains("reliancedigital")) return "reliancedigital";
        if (combined.Contains("myntra")) return "myntra";
        if (combined.Contains("bigbasket")) return "bigbasket";
        if (combined.Contains("swiggy")) return "swiggyinstamart";
        if (combined.Contains("croma")) return "croma";
        if (combined.Contains("vijaysales")) return "vijaysales";
        if (combined.Contains("jiomart")) return "jiomart";
        if (combined.Contains("tataneu") || combined.Contains("tatacliq") || combined.Contains("tata")) return "tataneu";
        return (websiteName ?? "unknown").ToLowerInvariant().Replace(" ", "");
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null) await _browser.CloseAsync();
        _playwright?.Dispose();
    }
}