using RepoRunner.Core;

namespace RepoRunner.Tests;

public class DiscoveryTests
{
    private static string Sample(TempHome home)
    {
        var root = home.Dir("repo");
        void Put(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Put(@"Portal\Components\Pages\Systems\Operations\DigitalForm\Steps\DigitalForm.razor", """
            @page "/ops/digitalform"
            <PaymentDetails MoveNext="Next" />
            <button @onclick="Reset">Start again</button>
            @code {
                private void Next() { }
                private void Reset() { }
            }
            """);
        Put(@"Portal\Components\Pages\Systems\Operations\DigitalForm\Steps\PaymentDetails.razor", """
            @inject IHttpHandler Http
            <button @onclick="ValidateBank">Check account</button>
            @code {
                private async Task ValidateBank()
                {
                    var user = EnvHelper.GetEnvironmentVariable(SysConstants.IPGWUsername);
                    await Http.GetData<Account>($"Validator/getAccDetails/{nssf}/{bank}", HttpEndPoints.OPSAPI, HttpRequestType.GET);
                    var request = new ExecuteQueryRequest { targetCommand = "RequestResponse_InsertRow", paramNames = new() };
                    await Http.PostOrUpdateData<ExecuteQueryRequest, Reply>(request, "Generic/Execute", HttpEndPoints.OPSAPI, HttpRequestType.POST);
                }
            }
            """);
        Put(@"Portal\Models\SysConstants.cs", """
            internal static class SysConstants
            {
                internal static readonly string IPGWUsername = "IPGWUname";
            }
            """);
        Put(@"Api\Controllers\ValidatorController.cs", """
            [Route("api/[controller]/[action]")]
            public class ValidatorController(IDatabaseHandler dbh) : BaseController
            {
                [HttpGet("{nssf}/{bank}")]
                public async Task<IActionResult> getAccDetails(string nssf, string bank)
                {
                    var esb = ValidationHelper.GetEnvironmentVariable("EsbUrl");
                    var url = esb + $"/SSPBankService?bank={bank}";
                    var reply = await new HttpClient().GetAsync(url);
                    await dbh.ExecuteAsync("BankChecks_InsertRow", null, System.Data.CommandType.StoredProcedure);
                    return Ok();
                }
            }
            """);
        Put(@"Api\Controllers\BaseController.cs", "public class BaseController : ControllerBase { }");
        return root;
    }

    [Fact]
    public void Finds_a_pages_steps_inside_the_components_it_is_built_from()
    {
        using var home = new TempHome();
        var map = Discovery.Scan(Sample(home));

        var form = Assert.Single(map.Processes, p => p.Kind == "page");
        Assert.Equal("Digital form", form.Name);
        Assert.Equal("Operations › Digital form", form.Area);

        var step = Assert.Single(form.Steps);
        Assert.Equal("Payment details: validate bank", step.Title);
        var sp = Assert.Single(step.Touches, t => t.Kind == "sp");
        Assert.Equal("RequestResponse_InsertRow", sp.Title);
        Assert.True(sp.Changes);
        Assert.Equal("OPSAPI", sp.Via); // handed to OpsApi, whose address is the generic runner
        var lookup = Assert.Single(step.Touches, t => t.Kind == "http");
        Assert.Equal(("OPSAPI", "Validator/getAccDetails/{nssf}/{bank}", "GET", false), (lookup.Title, lookup.Detail, lookup.Verb, lookup.Changes));
        Assert.Contains(step.Touches, t => t.Kind == "env" && t.Title == "IPGWUname"); // named through a constant
    }

    [Fact]
    public void Finds_endpoints_with_their_procedures_and_the_settings_their_addresses_come_from()
    {
        using var home = new TempHome();
        var map = Discovery.Scan(Sample(home));

        var endpoint = Assert.Single(map.Processes, p => p.Kind == "endpoint");
        Assert.Equal("/api/Validator/getAccDetails/{nssf}/{bank}", endpoint.Route);
        Assert.Equal("API · Validator", endpoint.Area);
        var touches = endpoint.Touches.ToList();
        Assert.Contains(touches, t => t.Kind == "sp" && t.Title == "BankChecks_InsertRow");
        Assert.Contains(touches, t => t.Kind == "http" && t.Detail == "{EsbUrl}/SSPBankService?bank={bank}");
    }

    [Fact]
    public void A_saved_scan_is_kept_until_the_code_changes()
    {
        using var home = new TempHome();
        var root = Sample(home);
        var file = Path.Combine(home.Dir("store"), "found.json");
        var map = Discovery.Scan(root);
        Discovery.Save(map, file);

        var loaded = Discovery.Load(file)!;
        Assert.Equal(map.Processes.Count, loaded.Processes.Count);
        Assert.Equal(loaded.Fingerprint, Discovery.Fingerprint(root, out _));

        File.AppendAllText(Path.Combine(root, @"Api\Controllers\BaseController.cs"), "\n// changed");
        Assert.NotEqual(loaded.Fingerprint, Discovery.Fingerprint(root, out _));
    }

    [Theory]
    [InlineData("SaveEditAsync", "Save edit")]
    [InlineData("OnHospitalChanged", "Hospital changed")]
    [InlineData("NoNinRegistrations", "No nin registrations")]
    [InlineData("getAccDetails", "Get acc details")]
    public void Names_read_as_words(string identifier, string words) => Assert.Equal(words, Discovery.Words(identifier));
}
