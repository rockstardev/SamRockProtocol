using System.Globalization;
using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Tests;
using BTCPayServer.Views.Stores;
using Microsoft.Playwright;
using NBitcoin;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Plugins.Tests;

public class PlaywrightBaseTest : UnitTestBase, IDisposable
{
    private string CreatedUser;
    private string InvoiceId;

    public PlaywrightBaseTest(ITestOutputHelper helper) : base(helper)
    {
    }

    public WalletId WalletId { get; set; }
    public IPlaywright Playwright { get; private set; }
    public IBrowser Browser { get; private set; }
    public IPage Page { get; private set; }
    public Uri ServerUri { get; private set; }
    public string Password { get; private set; }
    public string StoreId { get; private set; }
    public bool IsAdmin { get; private set; }

    public static bool IsRunningInCI =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    public void Dispose()
    {
        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch { }
        }

        Try(() =>
        {
            Page?.CloseAsync().GetAwaiter().GetResult();
            Page = null;
        });

        Try(() =>
        {
            Browser?.CloseAsync().GetAwaiter().GetResult();
            Browser = null;
        });

        Try(() =>
        {
            Playwright?.Dispose();
            Playwright = null;
        });
    }


    public async Task InitializePlaywright(ServerTester serverTester)
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        var launchOptions = new BrowserTypeLaunchOptions
        {
            Headless = true, // Set to true for CI and to false for real-time local testing 
            SlowMo = IsRunningInCI ? 100 : 50 // Delay to improve stability
        };
        if (serverTester.PayTester.InContainer)
            launchOptions.Args = new List<string>
            {
                "--disable-dev-shm-usage",
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-gpu"
            };
        Browser = await Playwright.Chromium.LaunchAsync(launchOptions);

        var context = await Browser.NewContextAsync();
        Page = await context.NewPageAsync();
        Page.SetDefaultTimeout(10000); // Set default timeout to 10 seconds (server startup can be slow)
        ServerUri = serverTester.PayTester.ServerUri;
        TestLogs.LogInformation($"Playwright: Browsing to {ServerUri}");
    }

    public async Task GoToUrl(string relativeUrl)
    {
        await Page.GotoAsync(Link(relativeUrl));
    }

    public string Link(string relativeLink)
    {
        return ServerUri.AbsoluteUri.WithoutEndingSlash() + relativeLink.WithStartingSlash();
    }


    public async Task LogIn(string user, string password = "123456")
    {
        await Page.Locator("#Email").FillAsync(user);
        await Page.Locator("#Password").FillAsync(password);
        await Page.Locator("#LoginButton").ClickAsync();
    }

    public async Task<string> RegisterNewUser(bool isAdmin = false)
    {
        var usr = RandomUtils.GetUInt256().ToString().Substring(64 - 20) + "@a.com";
        await Page.FillAsync("#Email", usr);
        await Page.FillAsync("#Password", "123456");
        await Page.FillAsync("#ConfirmPassword", "123456");
        if (isAdmin)
            await Page.ClickAsync("#IsAdmin");

        await Page.ClickAsync("#RegisterButton");
        CreatedUser = usr;
        Password = "123456";
        IsAdmin = isAdmin;
        return usr;
    }

    public async Task GoToStore(StoreNavPages storeNavPage = StoreNavPages.General)
    {
        await GoToStore(null, storeNavPage);
    }

    public async Task GoToStore(string storeId, StoreNavPages storeNavPage = StoreNavPages.General)
    {
        if (storeId is not null)
        {
            await GoToUrl($"/stores/{storeId}/");
            StoreId = storeId;
            if (WalletId != null)
                WalletId = new WalletId(storeId, WalletId.CryptoCode);
        }

        await Page.Locator($"#StoreNav-{storeNavPage}").ClickAsync();
    }

    public async Task<(string storeName, string storeId)> CreateNewStoreAsync(bool keepId = true)
    {
        if (await Page.Locator("#StoreSelectorToggle").IsVisibleAsync()) await Page.Locator("#StoreSelectorToggle").ClickAsync();
        await GoToUrl("/stores/create");
        var name = "Store" + RandomUtils.GetUInt64();
        TestLogs.LogInformation($"Created store {name}");
        await Page.FillAsync("#Name", name);

        var selectedOption = await Page.Locator("#PreferredExchange option:checked").TextContentAsync();
        Assert.Equal("Recommendation (Kraken)", selectedOption?.Trim());
        await Page.Locator("#PreferredExchange").SelectOptionAsync(new SelectOptionValue { Label = "CoinGecko" });
        await Page.ClickAsync("#Create");
        await Page.ClickAsync("#StoreNav-General");
        var storeId = await Page.InputValueAsync("#Id");
        if (keepId)
            StoreId = storeId;

        return (name, storeId);
    }

    public async Task GoToWalletSettingsAsync(string cryptoCode = "BTC")
    {
        await Page.ClickAsync($"#StoreNav-Wallet{cryptoCode}");
        var walletNavSettings = Page.Locator("#WalletNav-Settings");
        if (await walletNavSettings.CountAsync() > 0) await walletNavSettings.ClickAsync();
    }

    /// <summary>
    ///     Assume to be in store's settings
    /// </summary>
    /// <param name="cryptoCode"></param>
    /// <param name="derivationScheme"></param>
    public async Task AddDerivationScheme(string cryptoCode = "BTC",
        string derivationScheme = "tpubD6NzVbkrYhZ4XxNXjYTcRujMc8z8734diCthtFGgDMimbG5hUsKBuSTCuUyxWL7YwP7R4A5StMTRQiZnb6vE4pdHWPgy9hbiHuVJfBMumUu-[legacy]")
    {
        if (!(await Page.ContentAsync()).Contains($"Setup {cryptoCode} Wallet")) await GoToWalletSettingsAsync(cryptoCode);

        await Page.Locator("#ImportWalletOptionsLink").ClickAsync();
        await Page.Locator("#ImportXpubLink").ClickAsync();
        await Page.FillAsync("#DerivationScheme", derivationScheme);
        await Page.Locator("#Continue").ClickAsync();
        await Page.Locator("#Confirm").ClickAsync();
        await FindAlertMessageAsync();
    }

    public async Task<string> CreateInvoice(decimal? amount = 10, string currency = "USD",
        string refundEmail = "", string? defaultPaymentMethod = null,
        StatusMessageModel.StatusSeverity expectedSeverity = StatusMessageModel.StatusSeverity.Success)
    {
        return await CreateInvoice(null, amount, currency, refundEmail, defaultPaymentMethod, expectedSeverity);
    }

    public async Task<string> CreateInvoice(string storeId, decimal? amount = 10, string currency = "USD",
        string refundEmail = "", string? defaultPaymentMethod = null,
        StatusMessageModel.StatusSeverity expectedSeverity = StatusMessageModel.StatusSeverity.Success)
    {
        await GoToInvoices(storeId);

        await ClickPagePrimary();
        if (amount is decimal v)
            await Page.Locator("#Amount").FillAsync(v.ToString(CultureInfo.InvariantCulture));

        var currencyEl = Page.Locator("#Currency");
        await currencyEl.ClearAsync();
        await currencyEl.FillAsync(currency);
        await Page.Locator("#BuyerEmail").FillAsync(refundEmail);
        if (defaultPaymentMethod is not null)
            await Page.SelectOptionAsync("select[name='DefaultPaymentMethod']", new SelectOptionValue { Value = defaultPaymentMethod });
        await ClickPagePrimary();

        var statusText = (await FindAlertMessageAsync(expectedSeverity)).TextContentAsync();
        var inv = expectedSeverity == StatusMessageModel.StatusSeverity.Success
            ? Regex.Match(await statusText, @"Invoice (\w+) just created!").Groups[1].Value
            : null;

        InvoiceId = inv;
        TestLogs.LogInformation($"Created invoice {inv}");
        return inv;
    }

    private async Task GoToInvoices(string? storeId = null)
    {
        if (storeId is null)
        {
            await Page.Locator("#StoreNav-Invoices").ClickAsync();
        }
        else
        {
            await GoToUrl($"/stores/{storeId}/invoices/");
            StoreId = storeId;
        }
    }

    public async Task<ILocator> FindAlertMessageAsync(StatusMessageModel.StatusSeverity severity = StatusMessageModel.StatusSeverity.Success)
    {
        return await FindAlertMessageAsync(new[] { severity });
    }

    public async Task<ILocator> FindAlertMessageAsync(params StatusMessageModel.StatusSeverity[] severity)
    {
        var className = string.Join(", ", severity.Select(statusSeverity => $".alert-{StatusMessageModel.ToString(statusSeverity)}"));
        var locator = Page.Locator(className);
        try
        {
            await locator.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            var visibleElements = await locator.AllAsync();
            var visibleElement = visibleElements.FirstOrDefault(el => el.IsVisibleAsync().GetAwaiter().GetResult());
            if (visibleElement != null)
                return Page.Locator(className).First;

            return locator.First;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Unable to find {className}");
        }
    }

    public async Task<ILocator> FindAlertMessageAsync(StatusMessageModel.StatusSeverity[] severity, IPage page)
    {
        var className = string.Join(", ", severity.Select(statusSeverity => $".alert-{StatusMessageModel.ToString(statusSeverity)}"));
        var locator = page.Locator(className);
        try
        {
            await locator.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            var visibleElements = await locator.AllAsync();
            var visibleElement = visibleElements.FirstOrDefault(el => el.IsVisibleAsync().GetAwaiter().GetResult());
            if (visibleElement != null)
                return page.Locator(className).First;

            return locator.First;
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Unable to find {className}");
        }
    }

    public async Task ClickPagePrimary()
    {
        await Page.WaitForSelectorAsync("#page-primary", new PageWaitForSelectorOptions { State = WaitForSelectorState.Visible });
        await Page.Locator("#page-primary").ClickAsync();
    }

    public async Task InitializeBTCPayServer()
    {
        await GoToUrl("/register");
        await RegisterNewUser(true);
        await CreateNewStoreAsync();
        await GoToStore();
        await AddDerivationScheme();
    }
}
