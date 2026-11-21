using System.Text.Json.Nodes;
using RepoRunner.Core;

namespace RepoRunner.Tests;

public class SampleDataTests
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

        Put(@"Portal\Services\HttpHandler.cs", """
            public class HttpHandler : IHttpHandler
            {
                public async Task<T> GetItem<T>(string target, string targetId)
                {
                    var response = await new HttpClient().GetStringAsync("Generic/GetItem");
                    var obj = JsonConvert.DeserializeObject<GenericOpsApiResponse<T>>(response);
                    return obj.Result;
                }
            }
            public class GenericOpsApiResponse<T>
            {
                public T? Result { get; set; }
                public string? ErrorCode { get; set; }
                public string? ErrorDescription { get; set; }
            }
            public class PortalSystem
            {
                public string? SystemCode { get; set; }
                public string? SystemName { get; set; }
                [JsonProperty("logout")] public string? LogoutTime { get; set; }
                public int Order { get; set; }
            }
            """);
        Put(@"Portal\Services\SystemEntryService.cs", """
            public class SystemEntryService(IHttpHandler http)
            {
                public const string CatalogueTarget = "OpsPortalSystems";
                public async Task Load() => await http.GetItem<List<PortalSystem>>(CatalogueTarget, "ALL");
            }
            public static class AccCodes { public const string SystemCode = "ACC"; }
            """);
        return CodeIndex.Build(root);
    }

    [Fact]
    public void The_answer_a_call_expects_is_read_from_the_code_including_the_envelope_it_arrives_in()
    {
        using var home = new TempHome();
        Assert.Equal("GenericOpsApiResponse<List<PortalSystem>>", Sample(home).ResponseTypeFor("OpsPortalSystems"));
        Assert.Null(Sample(home).ResponseTypeFor("NobodyAsksThis"));
    }

    [Fact]
    public void A_made_up_answer_has_that_shape_and_one_item_per_known_code()
    {
        using var home = new TempHome();
        var code = Sample(home);
        var known = new Dictionary<string, string[]> { ["systemcode"] = ["ACC", "OPS"] };
        var answer = code.Sample(code.ResponseTypeFor("OpsPortalSystems")!, f => known.GetValueOrDefault(f.ToLowerInvariant()) ?? [])!.AsObject();

        Assert.Equal("0", (string)answer["errorCode"]!);
        var items = answer["result"]!.AsArray();
        Assert.Equal(["ACC", "OPS"], items.Select(i => (string)i!["systemCode"]!));
        Assert.Equal("ACC (sandbox)", (string)items[0]!["systemName"]!);
        Assert.Equal("30", (string)items[0]!["logout"]!); // the JSON name from the attribute
        Assert.Equal("1", items[0]!["order"]!.ToJsonString());
        Assert.Contains(code.Constants, c => c is ("SystemCode", "ACC"));
    }
}
