using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.Agents;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.AI;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using OpenDeepWiki.Tests.Services.Chat;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public sealed class ScanModuleCheckpointTests
{
    private sealed class TestContext(DbContextOptions<TestContext> options) : MasterDbContext(options);

    [Fact]
    public async Task RetryUsesValidatedModulesAndGenerationReadsTheirVersionedEvidence()
    {
        var calls = new ConcurrentQueue<string>();
        var generationCalls = new ConcurrentQueue<(string Root, string? PreviousDraft, string? Question)>();
        var failBeta = true;
        var truncateBetaReply = false;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.MapPost("/v1/chat/completions", async (HttpContext request) =>
        {
            var body = await JsonDocument.ParseAsync(request.Request.Body);
            var input = body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content");
            var text = input.ValueKind == JsonValueKind.String ? input.GetString() : input[0].GetProperty("text").GetString();
            var payload = JsonDocument.Parse(text!).RootElement;
            string reply;
            if (payload.TryGetProperty("module", out var module))
            {
                calls.Enqueue(module.GetString()!);
                reply = module.GetString() == "beta" && failBeta ? "{}" :
                    """{"title":"module","requiredFileIds":[0,9999],"questions":["entry?"],"dependencies":[],"topics":[]}""";
            }
            else
            {
                generationCalls.Enqueue((payload.GetProperty("root").GetString()!,
                    payload.GetProperty("previousDraft").GetString(),
                    payload.GetProperty("questions")[0].GetString()));
                var source = payload.GetProperty("sources")[0];
                var path = source.GetProperty("evidence").GetProperty("path").GetString();
                var markdown = path + " " + source.GetProperty("content").GetString();
                var expectsMarkdown = body.RootElement.GetProperty("messages")[0].GetRawText()
                    .Contains("END OF WIKI PAGE", StringComparison.Ordinal);
                if (payload.GetProperty("root").GetString() == "beta" && truncateBetaReply)
                {
                    truncateBetaReply = false;
                    reply = """{"markdown":"unfinished""";
                }
                else reply = expectsMarkdown
                    ? "# Recovered page\n\n" + markdown + "\n" + DeepRepositoryScan.PageEndMarker
                    : JsonSerializer.Serialize(new { markdown, unansweredQuestions = Array.Empty<string>() });
            }
            var chunk = JsonSerializer.Serialize(new { id = "chatcmpl-test", created = 1, model = "test",
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = reply }, finish_reason = "stop" } } });
            request.Response.ContentType = "text/event-stream";
            await request.Response.WriteAsync($"data: {chunk}\n\ndata: [DONE]\n\n");
        });
        await server.StartAsync();

        var root = Directory.CreateTempSubdirectory();
        try
        {
            var options = new DbContextOptionsBuilder<TestContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            using var context = new TestContext(options);
            var branch = new RepositoryBranch { Id = "branch", RepositoryId = "repo", BranchName = "main" };
            var language = new BranchLanguage { Id = "lang", RepositoryBranchId = branch.Id, LanguageCode = "en" };
            context.AddRange(branch, language);
            await context.SaveChangesAsync();
            var factory = new Mock<IContextFactory>();
            factory.Setup(f => f.CreateContext()).Returns(() => new TestContext(options));
            var settings = new Mock<IOptionsMonitor<WikiGeneratorOptions>>();
            settings.SetupGet(o => o.CurrentValue).Returns(new WikiGeneratorOptions { EnableAiScanProfile = true, ParallelCount = 1 });
            var resolver = new Mock<IAiProviderResolver>();
            var model = await TestAiProviderResolver.Instance.ResolveAsync(null, "test");
            resolver.Setup(r => r.ResolveAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(model with { BaseUrl = server.Urls.Single() + "/v1" });
            var scan = new DeepRepositoryScan(context, factory.Object, settings.Object, resolver.Object,
                new AgentFactory(Options.Create(new AiRequestOptions())), NullLogger<DeepRepositoryScan>.Instance);
            var workspace = new RepositoryWorkspace { WorkingDirectory = root.FullName, CommitId = "version-one" };
            foreach (var name in new[] { "alpha", "beta" })
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, name));
                File.WriteAllText(Path.Combine(root.FullName, name, "main.cs"), "version-one-evidence");
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => scan.EvaluateAsync(workspace, branch, true, default));
            Assert.Null(branch.DeepScanPlanJson);
            Assert.Equal("alpha", (await context.ScanModuleCheckpoints.SingleAsync()).ModuleRoot);

            failBeta = false;
            calls.Clear();
            var plan = await scan.EvaluateAsync(workspace, branch, true, default);
            Assert.Equal(new[] { "beta" }, calls.ToArray());
            Assert.Equal(1, plan.ReusedModules);
            File.WriteAllText(Path.Combine(root.FullName, "alpha", "main.cs"), "different-worktree-content");
            truncateBetaReply = true;
            await scan.GenerateAsync(workspace, language, plan, default);
            var docs = await context.DocFiles.ToListAsync();
            Assert.Equal(2, docs.Count);
            Assert.All(docs, doc => Assert.Contains("version-one-evidence", doc.Content));

            // Resume with one draft containing gaps and one missing page.
            var progress = DeepRepositoryScan.ReadProgress(language.DeepScanProgressJson)!;
            var alpha = progress.Batches.Single(batch => batch.Module == "alpha");
            var beta = progress.Batches.Single(batch => batch.Module == "beta");
            var alphaId = (await context.DocCatalogs.SingleAsync(row => row.Path == alpha.Page)).DocFileId;
            var betaCatalog = await context.DocCatalogs.SingleAsync(row => row.Path == beta.Page);
            betaCatalog.DocFileId = null;
            progress.Batches.Clear();
            progress.Batches.Add(alpha with { Gaps = ["specific gap"] });
            language.DeepScanProgressJson = JsonSerializer.Serialize(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await context.SaveChangesAsync();
            generationCalls.Clear();

            await scan.GenerateAsync(workspace, language, plan, default);

            Assert.Collection(generationCalls,
                first => Assert.Equal(("beta", null, "entry?"), first),
                second =>
                {
                    Assert.Equal("alpha", second.Root);
                    Assert.Contains("version-one-evidence", second.PreviousDraft);
                    Assert.Equal("specific gap", second.Question);
                });
            Assert.Equal(alphaId, (await context.DocCatalogs.SingleAsync(row => row.Path == alpha.Page)).DocFileId);
            Assert.NotNull(betaCatalog.DocFileId);

            // Older plans may contain empty files, whose page numbers must be preserved.
            var emptyModule = new ScanModule("empty", "Empty", ["empty.txt"], ["empty.txt"], ["entry?"], [], []);
            File.WriteAllText(Path.Combine(root.FullName, "empty.txt"), "");
            var emptyPage = "scan-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("empty")))[..16].ToLowerInvariant() + "/1";
            var legacyPlan = plan with { Modules = [.. plan.Modules, emptyModule] };
            var savedAlpha = await context.DocFiles.FindAsync(alphaId);
            var savedContent = savedAlpha!.Content;
            betaCatalog.DocFileId = null;
            const string missing = "模型未生成有效文档";
            progress.Batches.Clear();
            progress.Batches.AddRange([alpha with { Gaps = ["keep this draft"] }, beta with { Gaps = [missing] },
                new ScanBatchResult("empty", emptyPage, [], [missing], 0)]);
            language.DeepScanProgressJson = JsonSerializer.Serialize(progress, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await context.SaveChangesAsync();
            generationCalls.Clear();
            truncateBetaReply = true;

            await scan.GenerateAsync(workspace, language, legacyPlan, default);

            Assert.Equal(new[] { "beta", "beta" }, generationCalls.Select(call => call.Root));
            Assert.Equal(savedContent, savedAlpha.Content);
            var resumed = DeepRepositoryScan.ReadProgress(language.DeepScanProgressJson)!;
            Assert.NotNull(resumed.FinishedAt);
            Assert.True(resumed.Batches.Single(batch => batch.Page == emptyPage).SkippedEmpty);
            Assert.NotNull(betaCatalog.DocFileId);
            var recoveredBeta = await context.DocFiles.FindAsync(betaCatalog.DocFileId);
            Assert.Contains("# Recovered page", recoveredBeta!.Content);
            Assert.DoesNotContain(DeepRepositoryScan.PageEndMarker, recoveredBeta.Content);

            workspace.CommitId = "version-two";
            calls.Clear();
            var changed = await scan.EvaluateAsync(workspace, branch, true, default);
            Assert.Equal(0, changed.ReusedModules);
            Assert.Equal(new[] { "alpha", "beta" }, calls.ToArray());
        }
        finally
        {
            await server.StopAsync();
            root.Delete(true);
        }
    }
}
