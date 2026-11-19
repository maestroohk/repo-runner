using System.Text.Json;
using RepoRunner.Core;

namespace RepoRunner.Tests;

public class ResendTests
{
    [Theory]
    // Commands seen in real OpsPortal recordings
    [InlineData("POST", "RCU_SubmissionReviewHistory_Get", false)]
    [InlineData("POST", "RCU_Submission_ReviewItem", true)]
    [InlineData("POST", "RCU_Submission_Finalize", true)]
    [InlineData("POST", "RCU_Workflow_SubmitBundle", true)]
    [InlineData("POST", "RCU_Provision_Save", true)]
    [InlineData("POST", "PortalNotification_PublishScoped", true)]
    [InlineData("POST", "PortalNotification_UnreadCountScoped", false)]
    [InlineData("POST", "_GetPairedItems", false)]
    [InlineData("POST", "RCU_CONFIG_GET", false)]
    [InlineData("POST", "UserPermissions_GetActiveRolesByUserSystem", false)]
    [InlineData("POST", "SystemUser_Search", false)]
    [InlineData("POST", "RCU_Department_ListEligibleHods", false)]
    [InlineData("POST", "/OpsApis/api/Notifications/SendEmail", true)]
    [InlineData("GET", "/OpsApis/api/Generic/GetItem", false)]
    // Unknown names fall back on the method
    [InlineData("GET", "/data", false)]
    [InlineData("POST", "/data", true)]
    [InlineData("POST", "Something_Strange", true)]
    public void Classifies_lookups_and_changes(string method, string name, bool writes) =>
        Assert.Equal(writes, CallSafety.Writes(method, name));

    [Fact]
    public void Editor_hides_credentials_and_vault_secrets_and_puts_them_back()
    {
        var body = """{"targetCommand":"X_Get","paramValues":["129"],"apiUsername":"user1","apiPassword":"p@ss-word-1","note":"key=VAULTSECRET1 here"}""";
        var editable = new EditableBody(body, ["VAULTSECRET1"]);

        Assert.DoesNotContain("p@ss-word-1", editable.Text);
        Assert.DoesNotContain("VAULTSECRET1", editable.Text);
        Assert.Contains("\"apiUsername\": \"user1\"", editable.Text);

        // The user changes the id and leaves the bullets alone.
        var sent = editable.Restore(editable.Text.Replace("\"129\"", "\"130\""));
        using var doc = JsonDocument.Parse(sent);
        Assert.Equal("p@ss-word-1", doc.RootElement.GetProperty("apiPassword").GetString());
        Assert.Equal("key=VAULTSECRET1 here", doc.RootElement.GetProperty("note").GetString());
        Assert.Equal("130", doc.RootElement.GetProperty("paramValues")[0].GetString());
    }

    [Fact]
    public void Editor_sends_a_value_the_user_typed_over_the_bullets()
    {
        var editable = new EditableBody("""{"apiPassword":"old"}""", []);
        var sent = editable.Restore(editable.Text.Replace(EditableBody.Hidden, "new"));
        Assert.Equal("new", JsonDocument.Parse(sent).RootElement.GetProperty("apiPassword").GetString());
    }

    [Fact]
    public void Editor_rejects_broken_json_and_passes_plain_text_through()
    {
        var json = new EditableBody("""{"a":"b","pwd":"x"}""", []);
        Assert.Throws<VaultException>(() => json.Restore("{ not json"));

        var text = new EditableBody("name=bob&secret=S3CRET99", ["S3CRET99"]);
        Assert.DoesNotContain("S3CRET99", text.Text);
        Assert.Equal("name=bob&secret=S3CRET99", text.Restore(text.Text));
    }
}
