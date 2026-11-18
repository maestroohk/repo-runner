using System.Collections;
using RepoRunner.Core;

namespace RepoRunner.Tests;

public class ToolingTests
{
    [Fact]
    public void DotEnv_parses_the_shapes_found_in_the_repos()
    {
        var pairs = DotEnv.Parse("""
            # comment
            ASPNETCORE_ENVIRONMENT=Development

            export OpsApiBaseUrl = https://host/api?x=1&y=2
            mysql_validationDb="Server=db;Uid=u;Pwd=p=q;"
            Authentication__SigningKey='abc'
            EMPTY=
            not a pair
            """);
        Assert.Equal(5, pairs.Count);
        Assert.Equal("https://host/api?x=1&y=2", pairs.Single(p => p.Key == "OpsApiBaseUrl").Value);
        Assert.Equal("Server=db;Uid=u;Pwd=p=q;", pairs.Single(p => p.Key == "mysql_validationDb").Value);
        Assert.Equal("abc", pairs.Single(p => p.Key == "Authentication__SigningKey").Value);
        Assert.Equal("", pairs.Single(p => p.Key == "EMPTY").Value);
    }

    [Fact]
    public void Detector_finds_web_project_url_and_tailwind_companion_and_skips_tests()
    {
        using var home = new TempHome();
        var root = home.Dir("repo");
        var web = Directory.CreateDirectory(Path.Combine(root, "Portal")).FullName;
        Directory.CreateDirectory(Path.Combine(web, "Properties"));
        File.WriteAllText(Path.Combine(web, "Portal.csproj"), """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        File.WriteAllText(Path.Combine(web, "Properties", "launchSettings.json"), """
            {
              // comments are allowed here
              "profiles": {
                "IIS Express": { "commandName": "IISExpress" },
                "https": { "commandName": "Project", "applicationUrl": "https://localhost:7001;http://localhost:5001",
                           "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "Data_Key": "k" } },
              }
            }
            """);
        File.WriteAllText(Path.Combine(web, "package.json"), """{ "scripts": { "tw:watch": "tailwindcss --watch", "tw:build": "tailwindcss" } }""");
        File.WriteAllText(Path.Combine(web, ".env.example"), "OpsApiBaseUrl=\nEsbBaseUrl=\n");

        var lib = Directory.CreateDirectory(Path.Combine(root, "Portal.Library")).FullName;
        File.WriteAllText(Path.Combine(lib, "Portal.Library.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        var tests = Directory.CreateDirectory(Path.Combine(root, "Tests")).FullName;
        File.WriteAllText(Path.Combine(tests, "Tests.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="xunit" /></ItemGroup></Project>""");
        var ignored = Directory.CreateDirectory(Path.Combine(root, "node_modules", "x")).FullName;
        File.WriteAllText(Path.Combine(ignored, "package.json"), """{ "scripts": { "dev": "x" } }""");

        var result = Detector.Detect(root);

        Assert.Equal(["portal", "tw-watch"], result.Targets.Select(t => t.Name));
        Assert.Equal("portal", result.DefaultTarget);
        var portal = result.Targets[0];
        Assert.Equal(Path.Combine("Portal", "Portal.csproj"), portal.Project);
        Assert.Equal("https://localhost:7001;http://localhost:5001", portal.Urls);
        Assert.Equal(["tw-watch"], portal.Companions);
        Assert.Equal(["OpsApiBaseUrl", "EsbBaseUrl"], portal.Requires);
        Assert.Equal("node", result.Targets[1].Kind);
    }

    [Fact]
    public void LaunchSettings_variables_are_read_per_profile()
    {
        using var home = new TempHome();
        var file = Path.Combine(home.Path, "launchSettings.json");
        File.WriteAllText(file, """{ "profiles": { "a": { "environmentVariables": { "X": "1", "Flag": true } }, "b": {} } }""");
        var profiles = LaunchSettings.Read(file);
        Assert.Equal(2, profiles.Count);
        Assert.Equal("1", profiles[0].Variables.Single(v => v.Key == "X").Value);
        Assert.Equal("true", profiles[0].Variables.Single(v => v.Key == "Flag").Value);
        Assert.Empty(profiles[1].Variables);
    }

    [Fact]
    public void Environment_block_overlays_resolved_values_and_clean_drops_the_rest()
    {
        var shell = new Hashtable { ["PATH"] = @"C:\bin", ["OpsApiBaseUrl"] = "stale-machine-value", ["RANDOM_SHELL_VAR"] = "x", ["DOTNET_ROOT"] = "d" };
        var resolved = new Dictionary<string, string> { ["OPSAPIBASEURL"] = "from-vault", ["NEW"] = "n" };

        var inherited = Launcher.BuildEnvironment(resolved, clean: false, shell);
        Assert.Equal("from-vault", inherited["OpsApiBaseUrl"]);
        Assert.Equal("x", inherited["RANDOM_SHELL_VAR"]);
        Assert.Equal("n", inherited["NEW"]);

        var clean = Launcher.BuildEnvironment(resolved, clean: true, shell);
        Assert.Equal(@"C:\bin", clean["PATH"]);
        Assert.Equal("d", clean["DOTNET_ROOT"]);
        Assert.False(clean.ContainsKey("RANDOM_SHELL_VAR"));
        Assert.Equal("from-vault", clean["OpsApiBaseUrl"]);
    }

    [Fact]
    public async Task Child_receives_the_variables_and_secret_values_are_masked_in_output()
    {
        var env = Launcher.BuildEnvironment(new Dictionary<string, string> { ["RR_TEST_URL"] = "https://uat", ["RR_TEST_SECRET"] = "hunter2-secret" }, clean: false);
        var spec = Launcher.ForCommand("echo", "cmd", ["/c", "echo %RR_TEST_URL% %RR_TEST_SECRET%"], Path.GetTempPath());
        var output = new StringWriter();

        var exit = await Launcher.RunAsync([spec], env, ["hunter2-secret"], output, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("https://uat ****", output.ToString());
        Assert.DoesNotContain("hunter2-secret", output.ToString());
        Assert.Null(Environment.GetEnvironmentVariable("RR_TEST_SECRET")); // nothing leaked into this process
    }

    [Fact]
    public async Task Exit_code_of_the_main_process_is_returned()
    {
        var spec = Launcher.ForCommand("exit", "cmd", ["/c", "exit 7"], Path.GetTempPath());
        Assert.Equal(7, await Launcher.RunAsync([spec], Launcher.BuildEnvironment(new Dictionary<string, string>(), false), [], TextWriter.Null, CancellationToken.None));
    }
}
