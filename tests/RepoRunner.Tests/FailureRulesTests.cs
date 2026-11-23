using RepoRunner.App;

namespace RepoRunner.Tests;

public class FailureRulesTests
{
    private static int _ids;

    private static Flow Click(params (int? Status, string? Request, string? Response)[] calls)
    {
        var flow = new Flow { TraceId = "t" + Interlocked.Increment(ref _ids) };
        var root = new FlowStep { Id = "root", Name = "Event onclick -> App.Page.Save", Kind = "Internal", Start = DateTime.UtcNow };
        flow.Steps[root.Id] = root;
        var n = 0;
        foreach (var (status, request, response) in calls)
        {
            var step = new FlowStep { Id = "c" + n++, ParentId = "root", Kind = "Client", Start = DateTime.UtcNow.AddMilliseconds(n) };
            step.Tags["url.full"] = "https://api.test/OpsApis/api/Generic/ExecuteQueryFormated";
            step.Tags["http.request.method"] = "POST";
            if (status is { } code) step.Tags["http.response.status_code"] = code.ToString();
            else step.Status = "Error";
            flow.Steps[step.Id] = step;
            flow.Bodies[step.Id] = new FlowBody { Request = request, Response = response };
        }
        return flow;
    }

    private static string Command(string name) => $$"""{"targetCommand":"{{name}}","paramValues":["1"]}""";

    [Fact]
    public void A_clean_step_is_not_a_failure()
    {
        Assert.True(FlowRules.IsStage(Click()));
        Assert.Null(FlowRules.Failure(Click((200, Command("X_Get"), """{"errorCode":"0","errorDescription":"Success"}"""))));
    }

    [Fact]
    public void A_lookup_that_found_nothing_is_not_a_failure()
    {
        Assert.Null(FlowRules.Failure(Click((404, Command("RCU_Obligation_ListByProvision"), """{"errorCode":"100","errorDescription":"No data found"}"""))));
        Assert.Null(FlowRules.Failure(Click((200, Command("X_Get"), """{"errorCode":"100","errorDescription":"No data found"}"""))));
    }

    [Fact]
    public void Calls_that_failed_are_named_by_their_command()
    {
        Assert.Equal("RCU_Provision_Save failed on the other system (error 500)", FlowRules.Failure(Click((200, Command("X_Get"), "{}"), (500, Command("RCU_Provision_Save"), "oops"))));
        Assert.Equal("X_Get was refused (error 401: not allowed)", FlowRules.Failure(Click((401, Command("X_Get"), "{}"))));
        Assert.Equal("ExecuteQueryFormated got no answer", FlowRules.Failure(Click((null, null, null))));
        Assert.Equal("Member_Save reported error 7: Invalid NIN", FlowRules.Failure(Click((200, Command("Member_Save"), """{"errorCode":"7","errorDescription":"Invalid NIN"}"""))));
    }

    [Fact]
    public void An_unhandled_exception_is_a_failure_and_a_handled_one_is_not()
    {
        var handled = Click();
        handled.Errors.Add(new FlowError { At = DateTime.UtcNow, Type = "System.IO.IOException", Message = "x", Unhandled = false });
        Assert.Null(FlowRules.Failure(handled));

        var unhandled = Click();
        unhandled.Errors.Add(new FlowError
        {
            At = DateTime.UtcNow, Type = "System.NullReferenceException", Message = "x", Unhandled = true,
            Frames = [new FlowFrame("App.Page.Save", @"C:\src\Page.razor.cs", 42)],
        });
        Assert.Equal("The app crashed with NullReferenceException in Page.razor.cs, line 42", FlowRules.Failure(unhandled));
    }
}

public class BackgroundRoundTests
{
    private static Flow Call(string id, DateTime at, string command)
    {
        var flow = new Flow { TraceId = id, FirstSeen = at };
        var step = new FlowStep { Id = id + "-1", Name = "POST", Kind = "Client", Start = at, Ms = 40 };
        step.Tags["url.full"] = "https://opsapi.example/api/Generic/Execute";
        step.Tags["http.request.method"] = "POST";
        flow.Steps[step.Id] = step;
        flow.Bodies[step.Id] = new FlowBody { Request = $$"""{"targetCommand":"{{command}}"}""" };
        return flow;
    }

    [Fact]
    public void A_background_services_calls_are_grouped_into_rounds()
    {
        var start = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        var rounds = FlowRules.WithRounds([
            Call("a", start, "Stats_Get"), Call("b", start.AddSeconds(2), "Email_Send"),
            Call("c", start.AddSeconds(60), "Stats_Get")]);

        Assert.Equal(2, rounds.Count);
        Assert.All(rounds, r => Assert.True(FlowRules.IsStage(r)));
        Assert.Equal(2, rounds[0].Steps.Values.Count(s => s.Category == "http"));
        Assert.Equal(2, rounds[0].Bodies.Count);
        Assert.StartsWith(FlowRules.Round, rounds[1].Root!.Name);
    }

    [Fact]
    public void One_long_trace_is_still_split_where_the_service_pauses()
    {
        var start = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        var life = Call("life", start, "Stats_Get");
        var later = new FlowStep { Id = "life-2", Name = "POST", Kind = "Client", Start = start.AddSeconds(61), Ms = 30 };
        later.Tags["url.full"] = "https://opsapi.example/api/Generic/Execute";
        life.Steps[later.Id] = later;

        var rounds = FlowRules.WithRounds([life]);
        Assert.Equal(2, rounds.Count);
        Assert.NotEqual(rounds[0].TraceId, rounds[1].TraceId);
    }

    [Fact]
    public void An_app_with_requests_is_left_as_it_is()
    {
        var at = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);
        var request = new Flow { TraceId = "r", FirstSeen = at };
        var root = new FlowStep { Id = "r-1", Name = "GET /claims", Kind = "Server", Start = at };
        root.Tags["http.request.method"] = "GET";
        request.Steps[root.Id] = root;
        var flows = FlowRules.WithRounds([request, Call("a", at.AddSeconds(1), "Log_Ship")]);
        Assert.Equal(2, flows.Count);
        Assert.DoesNotContain(flows, f => f.TraceId.StartsWith("round-"));
    }
}
