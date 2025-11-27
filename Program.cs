
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using PuppeteerSharp;
using PuppeteerSharp.Input;
using SSM = System.ServiceModel;
using CWCF = CoreWCF;
using CoreWCF.Configuration;
// [ServiceContract], [OperationContract]
using CoreWCF.Channels;    // NetTcpBinding, EndpointAddress, ICommunicationObject
                           // ChannelFactory<T>

using Microsoft.SqlServer.Server;
using System.Runtime.Intrinsics.X86;
using System.ServiceModel;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices;
// using CoreWCF.Description; // only if you enable metadata behavior


class Program
{
    // Win32 API calls to control console window visibility
    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const int SW_HIDE = 0;

    private static readonly int port = 2207;
    private static readonly string host = Environment.MachineName;
    private static readonly string app = "LNAB";
    private static IBrowser? _browser;
    private static IPage? _page;
    private static string? _conn;
    private static bool _loggedIn;

    private static readonly string[] errorTerms = { "timeout", "due to planned maintenance", "the appres system is temporarily unavailable", "asas id enrollment not found" };
    // Wakes the processing loop immediately when a remote request arrives
    private static readonly SemaphoreSlim _kick = new(0, int.MaxValue);


    public static void SignalKick()
    {
        try { _kick.Release(); } catch { /* ignore */ }
    }
    // Listener URL used by external callers to reach this app
    private static string ListenerUrl => $"net.tcp://{host}:{port}/LNAB";

    static async Task Main()
    {
        _conn = System.Configuration.ConfigurationManager.ConnectionStrings["_conn"].ConnectionString;
        // Fire on abnormal exits
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ErrorLogger.Log(ex, "AppDomain.UnhandledException");
            }
            try { InActivateAsync(_conn).GetAwaiter().GetResult(); } catch { /* swallow on shutdown */ }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, __) =>
        {
            try { InActivateAsync(_conn).GetAwaiter().GetResult(); } catch { }
        };
        // Ctrl+C
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // we'll exit ourselves after cleanup
            try { InActivateAsync(_conn).GetAwaiter().GetResult(); } catch { }
            Environment.Exit(0);
        };
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            ErrorLogger.Log(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        var scheduleManager = new ScheduleManager(_conn);
        bool isAppActive = false; // To track the current state

        // Start the CoreWCF net.tcp listener
        _ = StartNetTcpHostAsync(port);

        await new BrowserFetcher().DownloadAsync();
        _browser = await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = false,
            Args = new[] { "--incognito" }   // whole browser runs in incognito
                                             // ⚠️ No UserDataDir here – incognito should not use a persistent profile
        });

        // Chrome usually opens one blank tab already – reuse it if present
        var pages = await _browser.PagesAsync();
        _page = pages.Length > 0 ? pages[0] : await _browser.NewPageAsync();

        // Set UA on that incognito tab
        await _page.SetUserAgentAsync(
            "Mozilla/5.0 (Windows NT 10.0; Win32; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome Safari"
        );

      //  _page = await _browser.NewPageAsync();
        await _page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win32; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome Safari");

        // Main scheduling and processing loop
        while (true)
        {
            try
            {
                scheduleManager.LoadSchedule();
                bool shouldBeActive = scheduleManager.IsWithinScheduledTime();

                if (shouldBeActive && !isAppActive)
                {
                    Console.WriteLine("[SCHEDULE] Active period started. Activating application...");
                    await ActivateAsync(_conn);
                    isAppActive = true;
                }
                else if (!shouldBeActive && isAppActive)
                {
                    Console.WriteLine("[SCHEDULE] Active period ended. Inactivating application...");
                    await InActivateAsync(_conn);
                    isAppActive = false;
                }

                if (isAppActive)
                {
                    Console.WriteLine("[SCHEDULE] Application is active. Checking for work...");
                    // Process pending requests first
                    string pendingRequest;
                    while (!string.IsNullOrEmpty(pendingRequest = ReportCompletedSearchesNotDistributed()))
                    {
                        MarkBusy();
                        await ProcessRequest(pendingRequest, true);
                        MarkAvailable();
                    }
                    while (!string.IsNullOrEmpty(pendingRequest = ReportUnCompletedSearches()))
                    {
                        MarkBusy();
                        await ProcessRequest(pendingRequest, false);
                        MarkAvailable();
                    }

                    // Wait for new requests with a 20-minute timeout for idle refresh
                    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    try
                    {
                        await _kick.WaitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine("[IDLE] No request received in 20 minutes. Refreshing browser...");
                        await _page.ReloadAsync();
                        continue; // Go back to the start of the loop to re-check schedule and for pending work
                    }

                    var prevSearch = ReportCompletedSearchesNotDistributed();
                    if (!string.IsNullOrEmpty(prevSearch))
                    {
                        MarkBusy();
                        await ProcessRequest(prevSearch, true);
                        MarkAvailable();
                    }
                    else
                    {
                        var newRequest = ReportUnCompletedSearches();
                        if (!string.IsNullOrEmpty(newRequest))
                        {
                            MarkBusy();
                            await ProcessRequest(newRequest, false);
                            MarkAvailable();
                        }
                    }
                }
                else
                {
                    var timeToNextTransition = scheduleManager.GetTimeToNextTransition();
                    Console.WriteLine($"[SCHEDULE] Application is inactive. Sleeping for {timeToNextTransition} until next schedule transition.");
                    await Task.Delay(timeToNextTransition);
                }
            }
            catch (Exception ex)
            {
                ErrorLogger.Log(ex, "MainProcessingLoop");
                await RestartAsync();
                return; // Exit the current process after initiating a restart
            }
        }
    }

    // ====== Login & Navigation ======
    private static async Task LoginAsync(IPage page, string username, string password, bool tickRememberCheckbox)
    {
        await SafeExecutor.RunAsync(async () =>
        {

            await page.DeleteCookieAsync();
            await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>
            {
                ["Cache-Control"] = "no-cache",
                ["Pragma"] = "no-cache"
            });
            // Navigate to the login page
            await page.GoToAsync("https://appres.alberta.ca/GOA.APPRES.Login/Login_Alberta.aspx", new NavigationOptions { Timeout = 30000 });

            await page.ClickAsync("#btnLgnUsingMADI");

            //// Wait for navigation or a specific element to confirm login success
            await page.WaitForNavigationAsync();



            await page.WaitForSelectorAsync("goa-button", new WaitForSelectorOptions { Visible = true });

            // Get the host <goa-button>
            var buttonHost = await page.QuerySelectorAsync("goa-button");

            // Check the real <button> inside the shadowRoot
            var isSignInButtonEnabled = await page.EvaluateFunctionAsync<bool>(@"(host) => {
    const btn = host.shadowRoot && host.shadowRoot.querySelector('button');
    if (!btn) return false;
    return !btn.disabled && btn.offsetParent !== null;
}", buttonHost);
            if (isSignInButtonEnabled)
            {


                var newContentSelector = "goa-form-item"; // Replace with the actual selector of the new content
                await page.WaitForSelectorAsync(newContentSelector, new WaitForSelectorOptions { Visible = true, Timeout = 1000 }); // Adjust timeout as needed


                var inputElement = await page.WaitForSelectorAsync("goa-input", new WaitForSelectorOptions { Visible = true, Timeout = 1000 }); // Adjust timeout as needed

                await inputElement.FocusAsync();

                //await inputElement.TypeAsync("UCDAON"); // Replace
                await page.Keyboard.TypeAsync(username, new TypeOptions { Delay = 100 });
                await Task.Delay(1000);


                await page.Keyboard.PressAsync("Enter");
                // wait for either error div or password field to appear
                await page.WaitForFunctionAsync(@"() => {
    return document.querySelector('div.error-msg') ||
           [...document.querySelectorAll('goa-input')]
             .some(el => el.shadowRoot?.querySelector('input[type=password]'));
}", new WaitForFunctionOptions { Timeout = 10000 });




                var button2 = await page.QuerySelectorAsync("goa-button");







                await page.WaitForSelectorAsync(newContentSelector, new WaitForSelectorOptions { Visible = true, Timeout = 30000 }); // Adjust timeout as needed
                inputElement = await page.WaitForSelectorAsync("goa-input", new WaitForSelectorOptions { Visible = true, Timeout = 30000 }); // Adjust timeout as needed

                await inputElement.FocusAsync();
                await Task.Delay(1000);

                await page.Keyboard.TypeAsync(password, new TypeOptions { Delay = 100 });
                await Task.Delay(1000);
                await page.Keyboard.PressAsync("Enter");

                await page.WaitForNavigationAsync();
                await Task.Delay(2000);
                await CheckForApplicationErrorsAsync(page);

            }
        });

    }

    private static async Task CheckForApplicationErrorsAsync(IPage page)
    {
        try
        {
            string pageContent = await page.EvaluateFunctionAsync<string>("() => document.body.innerText");
            foreach (var term in errorTerms)
            {
                if (pageContent.ToLower().Contains(term))
                {
                    throw new ApplicationException($"Error found: {term}");
                }
            }
        }
        catch (PuppeteerSharp.EvaluationFailedException ex) when (ex.Message.Contains("Execution context was destroyed"))
        {
            // This is a common race condition during navigation. Log it as a warning and continue.
            Console.WriteLine("[WARN] CheckForApplicationErrorsAsync: " + ex.Message);
        }
    }

    private static async Task CheckForApplicationErrorsAsync(IFrame frame)
    {
        try
        {
            string pageContent = await frame.EvaluateFunctionAsync<string>("() => document.body.innerText");
            foreach (var term in errorTerms)
            {
                if (pageContent.ToLower().Contains(term))
                {
                    throw new ApplicationException($"Error found: {term}");
                }
            }
        }
        catch (PuppeteerSharp.EvaluationFailedException ex) when (ex.Message.Contains("Execution context was destroyed"))
        {
            // This is a common race condition during navigation. Log it as a warning and continue.
            Console.WriteLine("[WARN] CheckForApplicationErrorsAsync: " + ex.Message);
        }
    }

    private static async Task<IPage> RegistrySection(IPage page)
    {

        await SafeExecutor.RunAsync(async () =>
        {
            // Log("Login Successful");

            await Task.Delay(2000);
            // Navigate to the Registry Page
            await page.GoToAsync("https://appres.alberta.ca/GOA.APPRES.Web/InitiateTransaction.aspx?ServiceTypeID=CAA45F61-80A1-4EEE-9ACD-B02B856678BC", new NavigationOptions { Timeout = 30000 });
            await Task.Delay(3000);
            // Define the src attribute of the iframe you want to access
            var frames = page.Frames;
            // Find the frame by its src attribute
            var iframeSrc = "https://appres.alberta.ca/GOA.APPRES.Web/InitiateTransaction.aspx?ServiceTypeID=CAA45F61-80A1-4EEE-9ACD-B02B856678BC"; // Replace with the actual src of the iframe




            var targetFrame = frames.FirstOrDefault(frame => frame.Url.Contains(iframeSrc));

            if (targetFrame != null)
            {
                await CheckForApplicationErrorsAsync(targetFrame);

                // Console.WriteLine($"Found the frame with src: {iframeSrc}");

                // Optionally, you can interact with the frame, e.g., by typing in an input field within the iframe
                var dropDownSelector = "#SearchRequest_serviceDropDown"; // Replace with the actual selector inside the iframe
                await targetFrame.WaitForSelectorAsync(dropDownSelector, new WaitForSelectorOptions { Visible = true });

                // Select the item by value
                var valueToSelect = "fdaf04e0-6828-4f8a-ae76-6f028150ab4d"; // Replace with the actual value you want to select
                await page.SelectAsync(dropDownSelector, valueToSelect);

                Console.WriteLine($"Selected the item with value: {valueToSelect}");

                // Optionally, you can take further actions like submitting a form or checking the result
                var submitButtonSelector = "#SearchRequest_GoButton"; // Replace with the actual selector
                await page.ClickAsync(submitButtonSelector);

                // Optionally wait for navigation or other actions to complete
                await page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle0 } });
                await Task.Delay(3000);
                // Optionally extract content from the new page
                var newPageContent = await page.GetContentAsync();
                await CheckForApplicationErrorsAsync(page);


                //Console.WriteLine(newPageContent);
            }
            else
            {
                Console.WriteLine("Iframe not found.");
            }
        });
        return page;

    }

    private static async Task<IPage> Search(IPage page, string vin)
    {
        await SafeExecutor.RunAsync(async () =>
        {
            // Define the class name to search within
            var className = "bandlight"; // Replace with the actual class name

            // Construct the CSS selector to find the first input inside the class
            var selector = $".{className} input";

            // Wait for the first input inside the class to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the first input element inside the class
            var inputElement = await page.QuerySelectorAsync(selector);
            await inputElement.FocusAsync();

            await inputElement.TypeAsync(vin);


            // Define the value of the button you want to click
            var buttonName = "Search"; // Replace with the actual value of the button

            // Construct the CSS selector to find the input by its value attribute
            selector = $"input[name='{buttonName}']";

            // Wait for the button to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the button by its value and click it
            var buttonElement = await page.QuerySelectorAsync(selector);


            // Click the button
            await buttonElement.ClickAsync();

            await page.WaitForNavigationAsync();
            await Task.Delay(3000);
            await CheckForApplicationErrorsAsync(page);
        });
        return (page);
    }

    private static async Task<IPage> ContinueSearch(IPage page)
    {
        await SafeExecutor.RunAsync(async () =>
        {

            // Define the value of the button you want to click
            var buttonName = "Continue"; // Replace with the actual value of the button

            // Construct the CSS selector to find the input by its value attribute
            var selector = $"input[value='{buttonName}']";

            // Wait for the button to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the button by its value and click it
            var buttonElement = await page.QuerySelectorAsync(selector);


            // Click the button
            await buttonElement.ClickAsync();

            await page.WaitForNavigationAsync();
            await Task.Delay(3000);
            await CheckForApplicationErrorsAsync(page);

        });
        return (page);

    }

    private static async Task<IPage> DistributeSearch(IPage page, string currentReqID, string vin)
    {

        await SafeExecutor.RunAsync(async () =>
        {
            int noOfLiens = 0;

            // Define the id of the checkbox element
            var checkboxId = "chkSpecificSerialNumberOnly"; // Replace with the actual id of the checkbox
            await page.WaitForSelectorAsync("#CriteriaPanelHolder input", new WaitForSelectorOptions { Timeout = 10000 });
            var tempVin = await page.EvaluateFunctionAsync<string>(
                @"() => {
        const el = document.querySelector('#CriteriaPanelHolder input');
        return el ? el.value ?? el.getAttribute('value') ?? null : null;
    }");

            if (!string.Equals(vin, tempVin, StringComparison.OrdinalIgnoreCase))
            {
                //Log($"VIN mismatch: expected '{tempVin}', got '{vin}'. Restarting app...");

                await RestartAsync();
            }
            // Construct the CSS selector to find the checkbox by its id
            var selector = $"#{checkboxId}";


            // Wait for the checkbox to be present and visible
            await page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Visible = true });


            // Click the checkbox
            await page.ClickAsync(selector);

            // Check for the "no results" message
            var noResultsElement = await page.QuerySelectorAsync(".alert.alert-info");
            string noResultsText = noResultsElement != null ? await noResultsElement.EvaluateFunctionAsync<string>("el => el.textContent") : string.Empty;

            bool noResultsFound = false;
            if (noResultsText.Contains("Your search did not return any results"))
            {
                noOfLiens = 0;
                noResultsFound = true;
            }
            else
            {
                // Define the id of the checkbox element
                var resultsId = "ResultGeneral"; // Replace with the actual id of the checkbox

                // Construct the CSS selector to find the checkbox by its id
                selector = $"#{resultsId}";

                // Wait for the checkbox to be present and visible
                await page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Visible = true });


                // Extract the text inside the <strong> tag within the <span> with id 'ResultGeneral'
                var strongText = await page.EvaluateFunctionAsync<string>(@"() => {
            const span = document.querySelector('#ResultGeneral');
            if (span) {
                const strongTag = span.querySelector('strong');
                return strongTag ? strongTag.textContent : null;
            }
            return null;
        }");


                // Display the extracted text
                if (strongText.Contains("Both") || strongText.Contains("Exact"))
                {
                    strongText = await page.EvaluateFunctionAsync<string>(@"() => {
            const span = document.querySelector('#ResultExact');
            if (span) {
                const strongTag = span.querySelector('strong');
                return strongTag ? strongTag.textContent : null;
            }
            return null;
        }");
                    // Regular expression to match text between two circular brackets
                    string pattern = @"\(([^)]+)\)";
                    Match match = Regex.Match(strongText, pattern);

                    if (match.Success)
                    {
                        string result = match.Groups[1].Value;
                        noOfLiens = Convert.ToInt32(result);
                        Console.WriteLine($"Text inside brackets: {result}");
                    }
                    else
                    {
                        Console.WriteLine("No text found inside brackets.");
                    }

                }
                else if (strongText.Contains("Inexact"))
                {
                    noOfLiens = 0;
                }
            }
            SQLSetCompleted(currentReqID, noOfLiens);

            if (noResultsFound)
            {
                return;
            }

            // Construct the CSS selector to find the input by its value attribute
            var buttonName = "Distribute";


            selector = $"input[name='{buttonName}']";

            // Wait for the button to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the button by its value and click it
            var buttonElement = await page.QuerySelectorAsync(selector);


            // Click the button
            await buttonElement.ClickAsync();

            await page.WaitForNavigationAsync();
            await Task.Delay(3000);
            await CheckForApplicationErrorsAsync(page);
        });
        return (page);
    }

    private static async Task<IPage> DistributeToEmail(IPage page, string email, string RId)
    {
        // Optionally extract content from the new page

        await SafeExecutor.RunAsync(async () =>
        {

            var buttonName = "ctrlPDD:PDDAddNew";


            var selector = $"input[name='{buttonName}']";

            // Wait for the button to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the button by its value and click it
            var buttonElement = await page.QuerySelectorAsync(selector);


            // Click the button
            await buttonElement.ClickAsync();

            await page.WaitForNavigationAsync();
            await CheckForApplicationErrorsAsync(page);
            // Define the selector for the dropdown (select) element
            var dropdownSelector = "#ctrlPDD_DDLControl"; // Replace with the actual selector for your dropdown



            // Wait for the dropdown to be present in the DOM
            await page.WaitForSelectorAsync(dropdownSelector);

            // Select the value from the dropdown
            var valueToSelect = "Email"; // Replace with the actual value attribute of the option you want to select
            await page.SelectAsync(dropdownSelector, valueToSelect);

            await page.WaitForNavigationAsync();
            await CheckForApplicationErrorsAsync(page);

            var inputName = "ctrlPDD:_txtEmailTo"; // Replace with the actual class name

            // Construct the CSS selector to find the first input inside the class
            selector = $"input[name='{inputName}']";

            // Wait for the first input inside the class to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the first input element inside the class
            var inputElement = await page.QuerySelectorAsync(selector);
            await inputElement.FocusAsync();

            await inputElement.TypeAsync(email);

            // Wait for the dropdown to be present in the DOM
            await page.WaitForSelectorAsync(dropdownSelector);

            inputName = "ctrlPDD:_txtEmailSubject"; // Replace with the actual class name

            // Construct the CSS selector to find the first input inside the class
            selector = $"input[name='{inputName}']";

            // Wait for the first input inside the class to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the first input element inside the class
            inputElement = await page.QuerySelectorAsync(selector);
            await inputElement.FocusAsync();

            await inputElement.TypeAsync(RId);

            // Query the button by its value and click it
            // buttonElement = await page.QuerySelectorAsync(selector);


            //// Click the button
            //await buttonElement.ClickAsync();

            //await page.WaitForNavigationAsync();
            //await Task.Delay(3000);
            buttonName = "ctrlPDD_btnSavePPD";


            selector = $"input[id='{buttonName}']";

            // Wait for the button to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the button by its value and click it
            buttonElement = await page.QuerySelectorAsync(selector);


            // Click the button
            await buttonElement.ClickAsync();

            await page.WaitForNavigationAsync();
            await CheckForApplicationErrorsAsync(page);

            // Define the value of the button you want to click
            buttonName = "Continue"; // Replace with the actual value of the button

            // Construct the CSS selector to find the input by its value attribute
            selector = $"input[value='{buttonName}']";

            // Wait for the button to be present and visible
            await page.WaitForSelectorAsync(selector);

            // Query the button by its value and click it
            buttonElement = await page.QuerySelectorAsync(selector);


            // Click the button
            await buttonElement.ClickAsync();

            await page.WaitForNavigationAsync();
            await Task.Delay(3000);

            var newPageContent = await page.GetContentAsync();
            await CheckForApplicationErrorsAsync(page);

        });
        return page;
    }

    private static async Task<bool> PreviousSearches(IPage page, string vin)
    {
        bool newRequest = true;
        await SafeExecutor.RunAsync(async () =>
        {
            bool result = false;
            await Task.Delay(2000);
            // Navigate to the Registry Page
            await page.GoToAsync("https://appres.alberta.ca/GOA.APPRES.Web/InitiateTransaction.aspx?ServiceTypeID=CAA45F61-80A1-4EEE-9ACD-B02B856678BC");
            await Task.Delay(3000);
            await CheckForApplicationErrorsAsync(page);
            // Define the src attribute of the iframe you want to access
            var frames = page.Frames;
            // Find the frame by its src attribute
            var iframeSrc = "https://appres.alberta.ca/GOA.APPRES.Web/InitiateTransaction.aspx?ServiceTypeID=CAA45F61-80A1-4EEE-9ACD-B02B856678BC"; // Replace with the actual src of the iframe

            var targetFrame = frames.FirstOrDefault(frame => frame.Url.Contains(iframeSrc));

            if (targetFrame != null)
            {
                //Console.WriteLine($"Found the frame with src: {iframeSrc}");

                // Optionally, you can take further actions like submitting a form or checking the result
                var submitButtonSelector = "#WcBrowsePerformedSearches_goButton"; // Replace with the actual selector
                await page.ClickAsync(submitButtonSelector);

                // Optionally wait for navigation or other actions to complete
                await page.WaitForNavigationAsync(new NavigationOptions { WaitUntil = new[] { WaitUntilNavigation.Load, WaitUntilNavigation.Networkidle0 } });
                await Task.Delay(3000);
                await CheckForApplicationErrorsAsync(page);
                // Optionally extract content from the new page

                // find undistributed search and click on launch button

                // Wait for the table row to be visible
                await page.WaitForSelectorAsync("tr");
                // Use querySelectorAll to get all rows in the table
                var rows = await page.QuerySelectorAllAsync("table tr");
                // Define the class name and the value you are looking for
                string targetValue = vin;
                // Iterate over each row and check the value in the 8th column
                foreach (var row in rows)
                {
                    // Select the 8th column <td> and get its text content
                    var cell = await row.QuerySelectorAsync("td:nth-child(8)");
                    if (cell != null)
                    {
                        var cellText = await page.EvaluateFunctionAsync<string>("el => el.textContent.trim()", cell);

                        // If the cell matches the value we're looking for
                        if (cellText == targetValue)
                        {
                            // Find the corresponding button in the last column and click it
                            var button = await row.QuerySelectorAsync("td:last-child input[type='submit']");
                            if (button != null)
                            {
                                await button.ClickAsync();
                                Console.WriteLine("Distribution Button clicked successfully.");
                                result = true;
                                newRequest = false;
                                //break; // Exit the loop after clicking the button
                            }
                            return;
                        }
                    }
                }

                if (result)
                {
                    Console.WriteLine("Match found!");
                }
                else
                {
                    Console.WriteLine("No match found in previous searches.");
                    newRequest = true;
                }
            }
            else
            {
                Console.WriteLine("Iframe not found.");
            }
        });

        return newRequest;
    }

    private static async Task ProcessRequest(string newRequest, bool prevSearch)
    {
        await SafeExecutor.RunAsync(async () =>
        {
            string email = System.Configuration.ConfigurationManager.AppSettings["Email"];
            string vin = newRequest.Substring(newRequest.IndexOf("_") + 1);
            string currentReqID = newRequest.Substring(0, newRequest.IndexOf("_"));

            if (!_loggedIn)
            {
                string username = System.Configuration.ConfigurationManager.AppSettings["Username"];
                string password = System.Configuration.ConfigurationManager.AppSettings["Password"];
                await LoginAsync(_page!, username, password, true);
                _loggedIn = true;
            }

            if (prevSearch)
            {
                bool isNewRequest = await PreviousSearches(_page!, vin);
                if (!isNewRequest)
                {
                    await DistributeSearch(_page!, currentReqID, vin);
                    await DistributeToEmail(_page!, email, currentReqID);
                }
            }
            else
            {
                await RegistrySection(_page!);
                await Search(_page!, vin);
                await ContinueSearch(_page!);
                await DistributeSearch(_page!, currentReqID, vin);
                await DistributeToEmail(_page!, email, currentReqID);
            }
            await Task.Delay(15000);
        });
    }

    private static (SSM.ChannelFactory<IService1Client> factory, IService1Client proxy) CreateClient(string url)
    {
        var binding = new SSM.NetTcpBinding(SSM.SecurityMode.None);
        var address = new SSM.EndpointAddress(url);
        var factory = new SSM.ChannelFactory<IService1Client>(binding, address);
        var proxy = factory.CreateChannel();
        return (factory, proxy);
    }


    private static void CloseClientSafe(SSM.ChannelFactory<IService1Client>? factory, IService1Client? proxy)
    {
        try { (proxy as SSM.IClientChannel)?.Close(); } catch { (proxy as SSM.IClientChannel)?.Abort(); }
        try { factory?.Close(); } catch { factory?.Abort(); }
    }

    private static async Task StartNetTcpHostAsync(int listenPort)
    {
        var webHost = new WebHostBuilder()
            .UseKestrel()
            .UseNetTcp(listenPort) // ✅ on IWebHostBuilder (correct place)
            .ConfigureServices(services =>
            {
                services.AddServiceModelServices();
                services.AddServiceModelMetadata();

                // Register the concrete service, not the interface
                services.AddSingleton<Service1>();
            })
            .Configure(app =>
            {
                app.UseServiceModel(s =>
                {
                    s.AddService<Service1>();

                    var binding = new CoreWCF.NetTcpBinding(CoreWCF.SecurityMode.None);
                    var baseAddress = new Uri($"net.tcp://0.0.0.0:{listenPort}/LNAB"); // or /LNAB if that’s your path
                    s.AddServiceEndpoint<Service1, IService1>(binding, baseAddress);
                });

                // ❌ REMOVE: app.UseNetTcp(listenPort);
            })
            .Build();

        Console.WriteLine($"[CoreWCF] Listening at net.tcp://0.0.0.0:{listenPort}/LNAB");
        await webHost.RunAsync();
    }

    private static async Task RestartAsync()
    {
        Console.WriteLine("[RESTART] Closing browser and relaunching…");
        try { if (_page is not null && !_page.IsClosed) await _page.CloseAsync(); } catch { }
        try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
        try { if (!string.IsNullOrEmpty(_conn)) await InActivateAsync(_conn!); } catch { }

        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(exe))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = exe, UseShellExecute = true });

        // Hide the current console window
        IntPtr hWnd = GetConsoleWindow();
        if (hWnd != IntPtr.Zero)
        {
            ShowWindow(hWnd, SW_HIDE);
        }

        Environment.Exit(0);
    }


    public static void RestartApp()
    {
        _ = RestartAsync();
    }

    private static async Task ActivateAsync(string connStr)
    {
        try
        {
            await using var cn = new SqlConnection(connStr);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.RegisteredApps WHERE Host = @h AND Port = @p AND App = @a)
    INSERT INTO dbo.RegisteredApps (Host, Port, App) VALUES (@h, @p, @a);", cn);
            cmd.Parameters.AddWithValue("@h", host);
            cmd.Parameters.AddWithValue("@p", port);
            cmd.Parameters.AddWithValue("@a", app);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"[ACTIVATE] Registered {host}:{port}/{app}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ACTIVATE-ERROR] {ex.Message}");
            throw;
        }
    }

    private static async Task InActivateAsync(string connStr)
    {
        try
        {
            await using var cn = new SqlConnection(connStr);
            await cn.OpenAsync();
            await using var cmd = new SqlCommand(@"
DELETE FROM dbo.RegisteredApps WHERE Host = @h AND Port = @p AND App = @a;", cn);
            cmd.Parameters.AddWithValue("@h", host);
            cmd.Parameters.AddWithValue("@p", port);
            cmd.Parameters.AddWithValue("@a", app);
            await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"[INACTIVATE] Deregistered {host}:{port}/{app}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[INACTIVATE-ERROR] {ex.Message}");
        }
    }

    public static class SafeExecutor
    {
        public static async Task RunAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception)
            {
                // Errors are now logged by the top-level handler in Main.
                // We just need to re-throw the exception to ensure the restart is triggered.
                throw;
            }
        }
    }

    public class ApplicationException : Exception
    {
        public ApplicationException(string message) : base(message) { }
    }


    // ====== DB methods ======

    private static void SQLSetCompleted(string currentReqID, int noOfLiens)
    {

        SqlConnection sqlConnection = new SqlConnection(_conn);
        DateTime now = DateTime.Now;
        string str = string.Format("Update OOPRequests Set EndTime='{0}', SubmittedTimes= {1} Where  RequestID = {2} and Province = 'AB'", now, noOfLiens, currentReqID);
        sqlConnection.Open();
        (new SqlCommand(str, sqlConnection)).ExecuteNonQuery();
        sqlConnection.Close();


    }


    private static void MarkAvailable()
    {
        try
        {

            using var connection = new SqlConnection(_conn);
            using var command = new SqlCommand(
                "UPDATE dbo.RegisteredApps SET StartTime = NULL WHERE Host = @Host AND Port = @Port AND App = @App",
                connection);

            command.Parameters.AddWithValue("@Host", host);
            command.Parameters.AddWithValue("@Port", port);
            command.Parameters.AddWithValue("@App", app);

            connection.Open();
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {

        }
    }
    private static void MarkBusy()
    {
        try
        {

            using var connection = new SqlConnection(_conn);
            using var command = new SqlCommand(
                "UPDATE dbo.RegisteredApps SET StartTime = @StartTime WHERE Host = @Host AND Port = @Port AND App = @App",
                connection);

            command.Parameters.AddWithValue("@StartTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("@Host", host);
            command.Parameters.AddWithValue("@Port", port);
            command.Parameters.AddWithValue("@App", app);

            connection.Open();
            command.ExecuteNonQuery();
        }
        catch (Exception)
        {

        }
    }

    private static string ReportUnCompletedSearches()
    {
        try
        {
            const string query = @"
            SELECT TOP 1 A.RequestID, B.VIN
            FROM OOPRequests A
            INNER JOIN Requests B ON A.RequestID = B.RequestID
            WHERE A.Completed = 0 
              AND A.DroneName IS NULL 
              AND Province = 'AB'";

            using var connection = new SqlConnection(_conn);
            using var command = new SqlCommand(query, connection);

            connection.Open();
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                string requestId = reader["RequestID"].ToString()?.Trim() ?? "";
                string vin = reader["VIN"].ToString()?.Trim() ?? "";
                return $"{requestId}_{vin}";
            }
        }
        catch (Exception)
        {

        }

        return string.Empty;
    }

    private static string ReportCompletedSearchesNotDistributed()
    {
        try
        {
            const string query = @"
            SELECT TOP 1 A.RequestID, B.VIN
            FROM OOPRequests A
            INNER JOIN Requests B ON A.RequestID = B.RequestID
            WHERE A.Completed = 1
              AND A.DroneName IS NOT NULL
              AND Province = 'AB'";

            using var connection = new SqlConnection(_conn);
            using var command = new SqlCommand(query, connection);

            connection.Open();
            using var reader = command.ExecuteReader();

            if (reader.Read())
            {
                string requestId = reader["RequestID"].ToString()?.Trim() ?? "";
                string vin = reader["VIN"].ToString()?.Trim() ?? "";
                return $"{requestId}_{vin}";
            }
        }
        catch (Exception)
        {

        }

        return string.Empty;
    }

}
