using System.Text.Json;
using pPdf.Services;

namespace pPdf.Tests;

public class UpdateTests
{
    static JsonElement Release(string tag, bool draft = false, bool prerelease = false, string host = "github.com", string? digest = null)
    {
        string digestJson = digest is null ? "" : $", \"digest\": \"{digest}\"";
        string json = $$"""
            {
              "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://github.com/FirekeeperPhate/pPdf/releases/tag/{{tag}}",
              "assets": [
                { "name": "pPdf-Setup-{{tag.TrimStart('v')}}-Full.exe", "size": 46000000,
                  "browser_download_url": "https://{{host}}/FirekeeperPhate/pPdf/releases/download/{{tag}}/pPdf-Setup-{{tag.TrimStart('v')}}-Full.exe"{{digestJson}} },
                { "name": "pPdf-Setup-{{tag.TrimStart('v')}}-Light.exe", "size": 4200000,
                  "browser_download_url": "https://{{host}}/FirekeeperPhate/pPdf/releases/download/{{tag}}/pPdf-Setup-{{tag.TrimStart('v')}}-Light.exe"{{digestJson}} }
              ]
            }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    static readonly Version Current = new(0, 1, 0);

    [Fact]
    public void Newer_release_offers_the_installer_of_the_same_edition()
    {
        var light = UpdateService.ParseRelease(Release("v0.1.1"), Current, fullEdition: false)!;
        Assert.Equal(new Version(0, 1, 1), light.Version);
        Assert.Equal("pPdf-Setup-0.1.1-Light.exe", light.InstallerName);
        Assert.Equal(4200000, light.InstallerSize);
        var full = UpdateService.ParseRelease(Release("v0.2.0"), Current, fullEdition: true)!;
        Assert.Equal("pPdf-Setup-0.2.0-Full.exe", full.InstallerName);
        Assert.EndsWith("/pPdf-Setup-0.2.0-Full.exe", full.InstallerUrl);
    }

    [Theory]
    [InlineData("v0.1.0")] // the same version
    [InlineData("v0.0.9")] // older
    [InlineData("latest")] // not a version
    public void Nothing_to_offer_for_the_same_or_an_older_version(string tag) =>
        Assert.Null(UpdateService.ParseRelease(Release(tag), Current, fullEdition: false));

    [Fact]
    public void Drafts_prereleases_and_foreign_downloads_are_ignored()
    {
        Assert.Null(UpdateService.ParseRelease(Release("v0.2.0", draft: true), Current, false));
        Assert.Null(UpdateService.ParseRelease(Release("v0.2.0", prerelease: true), Current, false));
        Assert.Null(UpdateService.ParseRelease(Release("v0.2.0", host: "example.com"), Current, false));
        Assert.NotNull(UpdateService.ParseRelease(Release("v0.2.0", host: "objects.githubusercontent.com"), Current, false));
    }

    [Fact]
    public void Checksum_is_taken_when_github_gives_it()
    {
        var update = UpdateService.ParseRelease(Release("v0.2.0", digest: "sha256:ABCDEF0123"), Current, false)!;
        Assert.Equal("ABCDEF0123", update.Sha256);
        Assert.Null(UpdateService.ParseRelease(Release("v0.2.0"), Current, false)!.Sha256);
    }

    [Fact]
    public void Release_without_the_editions_installer_offers_nothing()
    {
        var json = JsonDocument.Parse("""{ "tag_name": "v3.0.0", "assets": [ { "name": "notes.txt", "size": 1, "browser_download_url": "https://github.com/x" } ] }""");
        Assert.Null(UpdateService.ParseRelease(json.RootElement, Current, false));
    }
}
