using RepoRunner.Core;

namespace RepoRunner.Tests;

public class CodeIndexTests
{
    private static CodeIndex Sample(TempHome home)
    {
        var root = home.Dir("repo");
        void Put(string relative, string content)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        Put(@"Api\Controllers\ClaimsController.cs", """
            public class ClaimsController(IClaimService claims, ILogger<ClaimsController> log) : ControllerBase
            {
                [HttpPost("submit")]
                public async Task<IActionResult> Submit(ClaimRequest request)
                {
                    log.LogInformation("submitting");
                    if (request is null) throw new ArgumentException("request is required");
                    var result = await claims.SubmitAsync(request);
                    return Ok(result);
                }
            }
            """);
        Put(@"Api\Services\ClaimService.cs", """
            public interface IClaimService { Task<int> SubmitAsync(ClaimRequest request); }

            public class ClaimService : IClaimService
            {
                private readonly ClaimRepository _repository;
                private readonly HttpClient _http;

                public ClaimService(ClaimRepository repository, HttpClient http) { _repository = repository; _http = http; }

                public async Task<int> SubmitAsync(ClaimRequest request)
                {
                    var baseUrl = EnvHelper.GetEnvironmentVariable("EsbBaseUrl");
                    try
                    {
                        await _http.PostAsync(baseUrl + "/claims/notify", null);
                    }
                    catch (HttpRequestException)
                    {
                    }
                    Validate(request);
                    return await _repository.SaveAsync(request);
                }

                private void Validate(ClaimRequest request)
                {
                    if (request.Amount <= 0) throw new InvalidOperationException("amount");
                    Validate(request);
                }
            }
            """);
        Put(@"Api\Data\ClaimRepository.cs", """
            public class ClaimRepository
            {
                private readonly IConfiguration _configuration;
                public ClaimRepository(IConfiguration configuration) => _configuration = configuration;

                public async Task<int> SaveAsync(ClaimRequest request)
                {
                    using var connection = new MySqlConnection(_configuration["ConnectionStrings:Claims"]);
                    return await connection.ExecuteAsync("INSERT INTO claims (member, amount) VALUES (@Member, @Amount)", request);
                }
            }
            """);
        Put(@"Portal\Other\Claims.razor", "@code { private void Unrelated() { } }");
        Put(@"Portal\Pages\Claims.razor.cs", "namespace Portal.Pages; public partial class Claims { private void FromCodeBehind() { } }");
        Put(@"Portal\Pages\Claims.razor", """
            @page "/claims"
            @inject IClaimService Claims
            <button @onclick="Send">Send</button>

            @code {
                private ClaimRequest model = new();

                protected override async Task OnInitializedAsync()
                {
                    await Load();
                }

                private async Task Load() { }

                private async Task Send()
                {
                    await Claims.SubmitAsync(model);
                }
            }
            """);
        return CodeIndex.Build(root);
    }

    private static List<string> Flatten(CodeNode node, int depth = 0) =>
        new[] { $"{new string(' ', depth * 2)}{node.Kind}:{node.Title}{(node.Note is null ? "" : $" [{node.Note}]")}" }
            .Concat(node.Children.SelectMany(c => Flatten(c, depth + 1))).ToList();

    [Fact]
    public void Follows_an_endpoint_through_service_and_repository_and_marks_what_it_touches()
    {
        using var home = new TempHome();
        var trace = Sample(home).Trace("ClaimsController", "Submit");

        Assert.NotNull(trace);
        Assert.Equal(
        [
            "method:ClaimsController.Submit",
            "  throw:ArgumentException",
            "  method:ClaimService.SubmitAsync",
            "    env:EsbBaseUrl",
            "    http:PostAsync",
            "    catch:HttpRequestException",
            "    method:ClaimService.Validate",
            "      throw:InvalidOperationException",
            "      method:ClaimService.Validate [already on this path]",
            "    method:ClaimRepository.SaveAsync",
            "      config:ConnectionStrings:Claims",
            "      db:ExecuteAsync",
        ], Flatten(trace));

        var all = trace.All().ToList();
        Assert.Equal("empty catch: the error disappears", all.Single(n => n.Kind == "catch").Detail);
        Assert.StartsWith("INSERT INTO claims", all.Single(n => n.Kind == "db").Detail);
        Assert.Equal(Path.Combine("Api", "Services", "ClaimService.cs"), all.Single(n => n.Kind == "env").File);
        Assert.Equal(12, all.Single(n => n.Kind == "env").Line);
    }

    [Fact]
    public void Reads_a_razor_page_code_block_and_its_injected_services()
    {
        using var home = new TempHome();
        var index = Sample(home);

        var page = CodeIndex.ComponentKey(Path.Combine("Portal", "Pages", "Claims.razor"));
        // Its own block and its code-behind, but not the other page that happens to share the name.
        Assert.Equal(["FromCodeBehind", "Load", "OnInitializedAsync", "Send"], index.MethodsOf(page).Order());
        var send = index.Trace(page, "Send");
        Assert.NotNull(send);
        Assert.Equal("ClaimService.SubmitAsync", send.Children.Single().Title);
        Assert.Equal(15, send.Line); // line in the .razor file, not in the extracted block
        Assert.Equal("Claims.Send", send.Title);
        Assert.Null(index.Trace(page, "Missing"));
    }
}
