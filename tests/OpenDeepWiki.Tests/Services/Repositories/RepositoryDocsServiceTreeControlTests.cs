using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Moq;
using OpenDeepWiki.Cache.Abstractions;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Graphify;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryDocsServiceTreeControlTests
{
    private const string OwnerId = "owner-1";
    private const string OrgName = "demo";
    private const string RepoName = "repo-a";

    [Fact]
    public async Task GetTreeAsync_WhenAnonymous_CanControlIsFalse()
    {
        await using var context = CreateContext();
        SeedRepository(context, RepositoryStatus.Pending);
        var service = CreateService(context, new TestUserContext(userId: null));

        var response = await service.GetTreeAsync(OrgName, RepoName);

        Assert.False(response.CanControl);
    }

    [Fact]
    public async Task GetTreeAsync_WhenNonOwnerIsAuthenticated_CanControlIsFalse()
    {
        await using var context = CreateContext();
        SeedRepositoryWithDocument(context);
        var service = CreateService(context, new TestUserContext("stranger"));

        var response = await service.GetTreeAsync(OrgName, RepoName, "main", "en");

        Assert.False(response.CanControl);
        Assert.NotEmpty(response.Nodes);
    }

    [Fact]
    public async Task GetTreeAsync_WhenOwnerRequestsPendingRepository_CanControlIsTrue()
    {
        await using var context = CreateContext();
        SeedRepository(context, RepositoryStatus.Pending);
        var service = CreateService(context, new TestUserContext(OwnerId));

        var response = await service.GetTreeAsync(OrgName, RepoName);

        Assert.True(response.CanControl);
        Assert.Equal(RepositoryStatus.Pending, response.Status);
    }

    [Fact]
    public async Task GetTreeAsync_WhenOwnerRequestsCompletedRepository_CanControlIsTrue()
    {
        await using var context = CreateContext();
        SeedRepositoryWithDocument(context);
        var service = CreateService(context, new TestUserContext(OwnerId));

        var response = await service.GetTreeAsync(OrgName, RepoName, "main", "en");

        Assert.True(response.CanControl);
        Assert.NotEmpty(response.Nodes);
    }

    [Fact]
    public async Task GetTreeAsync_WhenAdminRequestsOtherUsersRepository_CanControlIsTrue()
    {
        await using var context = CreateContext();
        SeedRepositoryWithDocument(context);
        var service = CreateService(context, new TestUserContext("admin-1", isAdmin: true));

        var response = await service.GetTreeAsync(OrgName, RepoName, "main", "en");

        Assert.True(response.CanControl);
    }

    [Fact]
    public async Task GetTreeAsync_WhenRepositoryMissing_CanControlIsFalse()
    {
        await using var context = CreateContext();
        var service = CreateService(context, new TestUserContext(OwnerId));

        var response = await service.GetTreeAsync(OrgName, RepoName);

        Assert.False(response.Exists);
        Assert.False(response.CanControl);
    }

    private static RepositoryDocsService CreateService(TestDbContext context, IUserContext userContext)
    {
        return new RepositoryDocsService(
            context,
            new Mock<IGitPlatformService>(MockBehavior.Strict).Object,
            new Mock<ICache>(MockBehavior.Strict).Object,
            new Mock<IGraphifyArtifactService>(MockBehavior.Strict).Object,
            new RepositorySkillMarkdownBuilder(),
            userContext);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static void SeedRepository(TestDbContext context, RepositoryStatus status)
    {
        context.Repositories.Add(new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OrgName = OrgName,
            RepoName = RepoName,
            GitUrl = $"https://github.com/{OrgName}/{RepoName}.git",
            OwnerUserId = OwnerId,
            Status = status
        });
        context.SaveChanges();
    }

    private static void SeedRepositoryWithDocument(TestDbContext context)
    {
        var repositoryId = Guid.NewGuid().ToString();
        var branchId = Guid.NewGuid().ToString();
        var languageId = Guid.NewGuid().ToString();
        var docFileId = Guid.NewGuid().ToString();

        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OrgName = OrgName,
            RepoName = RepoName,
            GitUrl = $"https://github.com/{OrgName}/{RepoName}.git",
            OwnerUserId = OwnerId,
            Status = RepositoryStatus.Completed
        });

        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = "main"
        });

        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = languageId,
            RepositoryBranchId = branchId,
            LanguageCode = "en",
            IsDefault = true
        });

        context.DocFiles.Add(new DocFile
        {
            Id = docFileId,
            BranchLanguageId = languageId,
            Content = "# Overview"
        });

        context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = languageId,
            Path = "overview",
            Title = "Overview",
            Order = 1,
            DocFileId = docFileId
        });

        context.SaveChanges();
    }

    private sealed class TestUserContext(string? userId, bool isAdmin = false) : IUserContext
    {
        public string? UserId { get; } = userId;
        public string? UserName => "tester";
        public string? Email => "tester@example.com";
        public bool IsAuthenticated => !string.IsNullOrWhiteSpace(UserId);
        public ClaimsPrincipal? User { get; } = CreatePrincipal(userId, isAdmin);

        private static ClaimsPrincipal? CreatePrincipal(string? userId, bool isAdmin)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId)
            };
            if (isAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : OpenDeepWiki.EFCore.MasterDbContext(options);
}
