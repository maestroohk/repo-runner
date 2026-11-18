using RepoRunner.Core;

namespace RepoRunner.Tests;

public class FeatureScannerTests
{
    private static FeatureScan ScanSample(TempHome home)
    {
        var root = home.Dir("repo");
        void Put(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Put(@"Portal\Components\Pages\Claims\Index.razor", """
            @page "/claims"
            @page "/claims/{Id:int}"
            <h1>Claims</h1>
            """);
        Put(@"Api\Controllers\MembersController.cs", """
            namespace Api.Controllers;

            [ApiController]
            [Route("api/[controller]")]
            public class MembersController(IMembers members) : ControllerBase
            {
                [HttpGet]
                public async Task<IActionResult> List() => Ok();

                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok();

                [HttpPost("search"), Authorize]
                public IActionResult Search([FromBody] Query q) => Ok(EnvHelper.GetEnvironmentVariable("OpsApiBaseUrl"));

                [HttpDelete("/admin/members/{id}")]
                public IActionResult Remove(int id) => Ok();
            }
            """);
        Put(@"Api\Program.cs", """
            var key = Environment.GetEnvironmentVariable("Data_Key");
            var again = Environment.GetEnvironmentVariable("OPSAPIBASEURL");
            app.MapGet("/health", () => "ok");
            app.MapPost("/webhooks/{provider}", Handle);
            """);
        Put(@"Api\Workers\ClaimNotifier.cs", "public sealed class ClaimNotifier(ILogger<ClaimNotifier> log) : BackgroundService { }");
        Put(@"Api.Tests\FakeController.cs", "public class FakeController : ControllerBase { [HttpGet(\"nope\")] public IActionResult X() => Ok(); }");
        Put(@"Api\obj\Generated.cs", "app.MapGet(\"/generated\", () => 1);");
        return FeatureScanner.Scan(root);
    }

    [Fact]
    public void Finds_pages_endpoints_and_workers_and_skips_tests_and_build_output()
    {
        using var home = new TempHome();
        var scan = ScanSample(home);

        var routes = scan.Features.Where(f => f.Route is not null).Select(f => $"{f.Verb} {f.Route}").ToList();
        Assert.Contains("GET /claims", routes);
        Assert.Contains("GET /claims/{Id:int}", routes);
        Assert.Contains("GET /api/Members", routes);
        Assert.Contains("GET /api/Members/{id}", routes);
        Assert.Contains("POST /api/Members/search", routes);
        Assert.Contains("DELETE /admin/members/{id}", routes);
        Assert.Contains("GET /health", routes);
        Assert.Contains("POST /webhooks/{provider}", routes);
        Assert.DoesNotContain(routes, r => r.Contains("nope") || r.Contains("generated"));

        Assert.Equal("Members.Search", scan.Features.Single(f => f.Route == "/api/Members/search").Name);
        Assert.Equal("ClaimNotifier", scan.Features.Single(f => f.Kind == "worker").Name);
        Assert.Equal(1, scan.Features.Single(f => f.Route == "/claims").Line);
    }

    [Fact]
    public void Lists_each_variable_the_code_reads_once()
    {
        using var home = new TempHome();
        var scan = ScanSample(home);
        // Windows variable names ignore case, so the two spellings of OpsApiBaseUrl count as one.
        Assert.Equal(["DATA_KEY", "OPSAPIBASEURL"], scan.EnvReads.Select(r => r.Name.ToUpperInvariant()).Order());
    }

    [Fact]
    public void Matches_a_real_path_to_the_most_specific_feature()
    {
        using var home = new TempHome();
        var scan = ScanSample(home);

        Assert.Equal("/api/Members/search", scan.Match("POST", "/api/members/search")?.Route);
        Assert.Equal("/api/Members/{id}", scan.Match("GET", "/api/members/42")?.Route);
        Assert.Equal("/api/Members", scan.Match("GET", "/api/Members/")?.Route);
        Assert.Equal("/claims/{Id:int}", scan.Match("GET", "/claims/7")?.Route);
        Assert.Equal("/webhooks/{provider}", scan.Match("POST", "/webhooks/mtn")?.Route);
        Assert.Null(scan.Match("POST", "/api/members/42")); // only GET is declared for that route
        Assert.Null(scan.Match("GET", "/nothing/here"));
    }
}
