using System.Diagnostics.Tracing;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;


public class ApiService
{
    public static async Task<string> LoginAndGetSessionCookie(ServerInfo serverInfo)

    {
        HttpClient httpClient = new HttpClient();
        string username = serverInfo.Username;
        string password = serverInfo.Password;
        string apiUrl = serverInfo.Url;
        string rootPath = serverInfo.RootPath;

        string completeSessionCookie = string.Empty;
        var loginData = new
        {
            Username = username,
            Password = password
        };

        //check the databas if exist return
        var context = new UserDbContext();
        var dbCookie = await context.Cookies.FirstOrDefaultAsync(c => c.Url == apiUrl);
        if (dbCookie != null)
        {
            string cookie = dbCookie.SessionCookie;
            DateTimeOffset currentUtcTime = DateTimeOffset.UtcNow;


            if (dbCookie.ExpirationDate > currentUtcTime)
                return dbCookie.SessionCookie;
        }
        else if (dbCookie?.ExpirationDate < DateTimeOffset.UtcNow || dbCookie == null)
        {
            // valid nist 
            // Set the base address of your API
            httpClient.BaseAddress = new Uri(apiUrl);
            HttpResponseMessage response = await httpClient.PostAsJsonAsync($"/{rootPath}/login", loginData);

            if (response.IsSuccessStatusCode)
            {
                // Read the content as a string
                string responseContent = await response.Content.ReadAsStringAsync();

                // Retrieve the session cookie if present
                IEnumerable<string> setCookieHeaders;
                if (response.Headers.TryGetValues("Set-Cookie", out setCookieHeaders))
                {
                    // Extract the session cookie from the Set-Cookie header
                    completeSessionCookie = setCookieHeaders.FirstOrDefault(cookie => cookie.StartsWith("session"));
                }
                else
                {
                    // Handle the case where extraction fails
                    Console.WriteLine("Failed to extract expiration date from the cookie.");
                }

                // Do something with the session cookie, e.g., store it for future use
                //Console.WriteLine($"Complete Session Cookie: {completeSessionCookie}");
                TryExtractExpirationDate(completeSessionCookie, out var expirationDate);
                var purecookie = GetSessionCookie(completeSessionCookie);
                CookieData cookieData = new CookieData { Id = new Guid(), Url = apiUrl, ExpirationDate = expirationDate, SessionCookie = purecookie };
                await context.Cookies.AddAsync(cookieData);
                context.SaveChanges();
            }
            else
            {
                // Handle the case where the request was not successful
                Console.WriteLine($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                Console.WriteLine($"Error: there is a problem with retreieving or getting new cookie!");
            }
        }
        return GetSessionCookie(completeSessionCookie);
    }

    private static string GetSessionCookie(string cookie)
    {

        var cookieParts = cookie.Split(';');

        if (cookieParts[0].Trim().StartsWith("session="))
        {
            // Extract the main part of the session cookie
            return cookieParts[0].Trim().Substring("session=".Length);
        }

        // Return null or an empty string if the session cookie is not found
        return null;
    }

    //it called by methodes in program.cs
    public static async Task<bool> CreateUserAccount(AccountDto accountDto)
    {

        string sessionCookie = accountDto.SessionCookie;
        string selectedCountry = accountDto.SelectedCountry;
        string selectedPeriod = accountDto.SelectedPeriod;


        // Create an HttpClientHandler
        var handler = new HttpClientHandler();

        // Create a CookieContainer and add your cookie
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Uri(accountDto.ServerInfo.Url), new Cookie("session", accountDto.SessionCookie));

        // Assign the CookieContainer to the handler
        handler.CookieContainer = cookieContainer;

        // Create the HttpClient with the custom handler
        var httpClient = new HttpClient(handler);

        // Now you can use the httpClient to make requests with the specified cookie

        // Create the request body
        var inboundId = accountDto.ServerInfo.Inbounds.FirstOrDefault(i => i.Type == accountDto.AccType);
        if (inboundId == null) return false;
        Client client = new Client { TotalGB = ConvertGBToBytes(Convert.ToInt32(accountDto.TotoalGB)), ExpiryTime = DateTime.Now.AddDays(ConvertPeriodToDays(accountDto.SelectedPeriod)) };
        var requestBody = new
        {
            id = inboundId.Id,
            settings = Client.MakeSettingString(client)
        };

        var apiUrl = accountDto.ServerInfo.Url + "/" + accountDto.ServerInfo.RootPath + "/" + "panel/api/inbounds/addClient";

        try
        {
            // Send the POST request with the JSON body
            HttpResponseMessage response = await httpClient.PostAsJsonAsync(apiUrl, requestBody);
            string responseBody = await response.Content.ReadAsStringAsync();
            AddClientResult result = JsonConvert.DeserializeObject<AddClientResult>(responseBody);


            // Check if the request was successful
            if (response.IsSuccessStatusCode && result.Success)
            {
                // Account created successfully
                // Read and print the response content
                if (accountDto.AccType == "tunnel")
                {

                    var configLink = accountDto.ServerInfo.VmessTemplate;
                    configLink.Ps += client.Email;
                    configLink.Id = client.Id;

                    //Console.WriteLine(responseBody);
                    UserDbContext _userDbContext = new UserDbContext();

                    await _userDbContext.SaveUserStatus(new User { Id = accountDto.TelegramUserId, ConfigLink = configLink.ToVMessLink(), Email = client.Email });
                    await _userDbContext.SaveChangesAsync();
                    return true;
                }
                else if (accountDto.AccType == "realityv6")
                {
                    var vlessLink = $"vless://{client.Id}@{accountDto.ServerInfo.Vless.Domain}:443?type=tcp&security=reality&fp=firefox&pbk=kGzzo-8w_p6XHOyF1Pr1jiGjgqjICkWJyNw7ksML3yY&sni=www.google-analytics.com&sid=6c0eefcb#RealityMTN-{client.Email}";
                    UserDbContext _userDbContext = new UserDbContext();

                    await _userDbContext.SaveUserStatus(new User { Id = accountDto.TelegramUserId, ConfigLink = vlessLink, Email = client.Email });
                    await _userDbContext.SaveChangesAsync();
                    return true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                Console.WriteLine($"Error: {response.StatusCode} - {response.ReasonPhrase}");
                Console.WriteLine($"Server Message:  isSuccessful:{result.Success} - {result.Msg}");
                return false;
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HttpRequestException: {ex.Message}");

        }
        return false;
    }

    private static bool TryExtractExpirationDate(string cookie, out DateTimeOffset expirationDate)
    {
        expirationDate = DateTimeOffset.MinValue; // Default value if extraction fails


        var expirationDateIndex = cookie.IndexOf("Expires=");

        if (expirationDateIndex != -1)
        {
            var expirationDateString2 = cookie.Substring(expirationDateIndex + "Expires=".Length).Trim();

            Console.WriteLine("=================================");
            Console.WriteLine("This is expirationDateString :");
            Console.WriteLine(expirationDateString2);
            Console.WriteLine("=================================");


            string dateString = expirationDateString2;

            string[] dateParts = dateString.Split(';');

            // Extract the date part
            string expirationDateString = dateParts[0].Trim();

            // Declare the expirationDate variable outside the if block
            DateTimeOffset expirationDateeee;

            // Parse the date string
            if (DateTimeOffset.TryParseExact(expirationDateString, "ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out expirationDateeee))
            {
                // Successfully parsed, use the expirationDate
                Console.WriteLine($"Expiration Date: {expirationDateeee}");
                expirationDate = expirationDateeee;
            }
            else
            {
                // Parsing failed, handle the error
                Console.WriteLine("Failed to parse the expiration date from the cookie.");
                // Set a default or handle the error as needed
                expirationDate = DateTimeOffset.UtcNow; // For example, set to the current UTC time
            }

            // Now, expirationDate contains the parsed or default value

        }

        return false; // Extraction failed
    }

    public static string CreateAddAccountRequestBody(string settingsSection)
    {
        // Assuming you have a class representing the settings structure
        var settings = JsonConvert.DeserializeObject<Settings>(settingsSection);

        // Assuming you have an AddAccountRequest class for the request body
        var addAccountRequest = new AddAccountRequest
        {
            // Populate properties based on the settings
            // Adjust this part according to your actual class structure
            Id = settings.Id,
            Clients = settings.Clients
        };

        // Serialize the AddAccountRequest object to JSON
        string requestBody = JsonConvert.SerializeObject(addAccountRequest);

        return requestBody;
    }
    public static async Task<ClientExtend> FetchClientFromServer(Guid id, ServerInfo serverInfo, int inboundId)
    {
        Client findedClient = null;
        // login
        var sessionCookie = await LoginAndGetSessionCookie(serverInfo);
        if (sessionCookie == null) throw new Exception("Error in login and get session cookie");

        //1.fetch inbound data
        var inboundstateUrl = serverInfo.Url + "/" + serverInfo.RootPath + "/panel/api/inbounds/get/" + inboundId.ToString();
        //InboundState apiResponse = JsonConvert.DeserializeObject<InboundState>(jsonResponse);


        // Create an HttpClientHandler
        var handler = new HttpClientHandler();

        // Create a CookieContainer and add your cookie
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Uri(serverInfo.Url), new Cookie("session", sessionCookie));

        // Assign the CookieContainer to the handler
        handler.CookieContainer = cookieContainer;

        // Create the HttpClient with the custom handler
        var httpClient = new HttpClient(handler);

        // Now you can use the httpClient to make requests with the specified cookie
        InboundState result = null;
        try
        {
            // Send the Get request 
            HttpResponseMessage response = await httpClient.GetAsync(inboundstateUrl);
            string responseBody = await response.Content.ReadAsStringAsync();
            result = JsonConvert.DeserializeObject<InboundState>(responseBody);
            //result.ServerInfoObject.Settings;

        }
        catch
        {
        }
        if (result == null) return null;

        // Deserialize the JSON string to a JObject
        JObject jsonObject = JsonConvert.DeserializeObject<JObject>(result.ServerInfoObject.Settings);
        // Access the "clients" array
        JArray clientsArray = jsonObject["clients"] as JArray;
        if (clientsArray != null)
        {
            // Convert the JArray to a list of objects
            List<Client> clients = clientsArray.ToObject<List<Client>>();
            findedClient = clients.FirstOrDefault(c => c.Id == id);
        }
        if (findedClient == null) return null;


        inboundstateUrl = serverInfo.Url + "/" + serverInfo.RootPath + "/panel/api/inbounds/getClientTraffics/" + findedClient.Email;
        ClientState clientState = null;
        //2.fetch client stat
        try
        {
            // Send the Get request 
            HttpResponseMessage response = await httpClient.GetAsync(inboundstateUrl);
            string responseBody = await response.Content.ReadAsStringAsync();
            clientState = JsonConvert.DeserializeObject<ClientState>(responseBody);
            //result.ServerInfoObject.Settings;

        }
        catch
        {
        }
        if (clientState == null) return null;
        ClientExtend client = new ClientExtend
        {
            Id = findedClient.Id,
            Email = findedClient.Email,
            TotalGB = clientState.ClientStateObject.Total,
            ExpiryTime = findedClient.ExpiryTime,
            SubId = findedClient.SubId,
            Enable = findedClient.Enable,
            Down = clientState.ClientStateObject.Down,
            Up = clientState.ClientStateObject.Up,
            InboundId = clientState.ClientStateObject.InboundId
        };


        return client;

    }
    public static long ConvertGBToBytes(int gigabytes)
    {
        const long bytesPerGB = 1024L * 1024L * 1024L;
        return gigabytes * bytesPerGB;
    }

    static long ConvertDateTimeToTimestamp(DateTime dateTime)
    {
        // Assuming the input DateTime is in UTC to match the timestamp provided
        DateTime unixEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan timeDifference = dateTime.ToUniversalTime() - unixEpoch;

        // Return the timestamp in seconds
        return (long)timeDifference.TotalSeconds;
    }
    public static int ConvertPeriodToDays(string period)
    {
        switch (period)
        {
            case "1 Month":
                return 30; // Assuming one month is approximately 30 days
            case "2 Months":
                return 60; // Assuming two months is approximately 60 days
            case "3 Months":
                return 90; // Assuming three months is approximately 90 days
            case "6 Months":
                return 180; // Assuming six months is approximately 180 days
            default:
                // Handle other cases or throw an exception if needed
                throw new ArgumentException($"Invalid period: {period}");
        }
    }
}

