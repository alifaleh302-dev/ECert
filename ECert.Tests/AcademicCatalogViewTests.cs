using System.Net;
using System.Text.RegularExpressions;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// The academic catalog is one tree plus one detail panel: selecting a node is a link
/// (<c>?selected=kind:id</c>) and "add here" is a link with the parent already known
/// (<c>?add=kind&amp;parentId=id</c>). These tests drive the real HTTP surface so a node can never
/// disappear from the tree, a form can never post to the wrong parent, and creating an item lands
/// the user on that item instead of an anonymous list.
/// </summary>
[Collection(AppCollection.Name)]
public class AcademicCatalogViewTests
{
    private readonly AppFixture _fixture;

    public AcademicCatalogViewTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Selecting_a_level_renders_its_path_in_the_tree_and_its_form()
    {
        var admin = await SignInAdminAsync();
        var chain = await CreateChainAsync(admin);

        var html = WebUtility.HtmlDecode(
            await admin.GetStringAsync($"/AcademicCatalog?selected=level:{chain.LevelId}"));

        Assert.Contains(chain.University, html);
        Assert.Contains(chain.College, html);
        Assert.Contains(chain.Specialization, html);
        Assert.Contains(chain.Level, html);

        Assert.Contains("class=\"ac-node__row is-selected\"", html);
        Assert.Contains("action=\"/AcademicCatalog/UpdateLevel\"", html);
        Assert.Contains($"name=\"academicLevelOptionId\" value=\"{chain.LevelId}\"", html);
        Assert.Contains("name=\"sortOrder\"", html);
    }

    [Fact]
    public async Task Selecting_a_college_shows_its_children_and_links_them()
    {
        var admin = await SignInAdminAsync();
        var chain = await CreateChainAsync(admin);

        var html = WebUtility.HtmlDecode(
            await admin.GetStringAsync($"/AcademicCatalog?selected=college:{chain.CollegeId}"));

        Assert.Contains("action=\"/AcademicCatalog/UpdateCollege\"", html);
        Assert.Contains($"name=\"universityId\" value=\"{chain.UniversityId}\"", html);
        Assert.Contains(chain.Specialization, html);
        Assert.Contains("class=\"ac-child\"", html);
    }

    [Fact]
    public async Task Adding_a_child_prefills_the_parent()
    {
        var admin = await SignInAdminAsync();
        var chain = await CreateChainAsync(admin);

        var html = WebUtility.HtmlDecode(
            await admin.GetStringAsync($"/AcademicCatalog?add=college&parentId={chain.UniversityId}"));

        Assert.Contains("action=\"/AcademicCatalog/CreateCollege\"", html);
        Assert.Contains($"name=\"universityId\" value=\"{chain.UniversityId}\"", html);
        Assert.Contains(chain.University, html);
        Assert.DoesNotContain("اختر الجامعة", html);
    }

    [Fact]
    public async Task Creating_an_item_lands_on_the_new_item()
    {
        var admin = await SignInAdminAsync();
        var chain = await CreateChainAsync(admin);
        var name = $"كلية جديدة {Guid.NewGuid().ToString("N")[..6]}";

        var response = await admin.PostFormAsync("/AcademicCatalog", "/AcademicCatalog/CreateCollege",
            new Dictionary<string, string>
            {
                ["universityId"] = chain.UniversityId.ToString(),
                ["collegeName"] = name
            });

        response.EnsureSuccessStatusCode();
        var query = Uri.UnescapeDataString(response.RequestMessage!.RequestUri!.Query);
        Assert.Contains("selected=college:", query);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains(name, html);
        Assert.Contains("class=\"ac-node__row is-selected\"", html);
    }

    [Fact]
    public async Task Unknown_selection_falls_back_to_the_first_university()
    {
        var admin = await SignInAdminAsync();
        var chain = await CreateChainAsync(admin);

        var response = await admin.GetAsync("/AcademicCatalog?selected=college:999999");
        response.EnsureSuccessStatusCode();

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("class=\"ac-node__row is-selected\"", html);
        Assert.DoesNotContain("action=\"/AcademicCatalog/UpdateCollege\"", html);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private sealed record Chain(
        string University, int UniversityId,
        string College, int CollegeId,
        string Specialization, int SpecializationId,
        string Level, int LevelId);

    private async Task<Chain> CreateChainAsync(AppClient admin)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var university = $"جامعة اختبار {suffix}";
        var college = $"كلية اختبار {suffix}";
        var specialization = $"تخصص اختبار {suffix}";
        var level = $"مستوى اختبار {suffix}";

        var universityResponse = await admin.PostFormAsync("/AcademicCatalog", "/AcademicCatalog/CreateUniversity",
            new Dictionary<string, string> { ["universityName"] = university });
        universityResponse.EnsureSuccessStatusCode();
        var universityId = SelectedId(universityResponse, "university");

        var collegeResponse = await admin.PostFormAsync("/AcademicCatalog", "/AcademicCatalog/CreateCollege",
            new Dictionary<string, string>
            {
                ["universityId"] = universityId.ToString(),
                ["collegeName"] = college
            });
        collegeResponse.EnsureSuccessStatusCode();
        var collegeId = SelectedId(collegeResponse, "college");

        var specializationResponse = await admin.PostFormAsync("/AcademicCatalog", "/AcademicCatalog/CreateSpecialization",
            new Dictionary<string, string>
            {
                ["collegeId"] = collegeId.ToString(),
                ["specializationName"] = specialization
            });
        specializationResponse.EnsureSuccessStatusCode();
        var specializationId = SelectedId(specializationResponse, "specialization");

        var levelResponse = await admin.PostFormAsync("/AcademicCatalog", "/AcademicCatalog/CreateLevel",
            new Dictionary<string, string>
            {
                ["academicSpecializationId"] = specializationId.ToString(),
                ["levelName"] = level,
                ["sortOrder"] = "1"
            });
        levelResponse.EnsureSuccessStatusCode();
        var levelId = SelectedId(levelResponse, "level");

        return new Chain(university, universityId, college, collegeId, specialization, specializationId, level, levelId);
    }

    /// <summary>Reads the id the create action selected, e.g. <c>?selected=college:42</c>.</summary>
    private static int SelectedId(HttpResponseMessage response, string kind)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var query = Uri.UnescapeDataString(response.RequestMessage!.RequestUri!.Query);
        var match = Regex.Match(query, $"{kind}:(\\d+)");
        Assert.True(match.Success, $"expected {kind}:<id> in {query}");
        return int.Parse(match.Groups[1].Value);
    }
}
