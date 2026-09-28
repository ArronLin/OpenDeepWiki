using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitHub;
using OpenDeepWiki.Services.Organizations;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryServiceProcessingControlEndpointTests
{
    private const string OwnerId = "owner-1";
    private const string RepositoryId = "repo-1";
    private const string OrgName = "demo";
    private const string RepoName = "repo-a";

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task ControlEndpoints_WhenAnonymous_ReturnUnauthorized(string action)
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Pending);
        var service = CreateService(context, userId: null);

        var result = await InvokeAsync(service, action);

        Assert.Equal(StatusCodes.Status401Unauthorized, GetStatusCode(result));
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task ControlEndpoints_WhenRepositoryMissing_ReturnNotFound(string action)
    {
        using var context = CreateContext();
        var service = CreateService(context, OwnerId);

        var result = await InvokeAsync(service, action);

        Assert.Equal(StatusCodes.Status404NotFound, GetStatusCode(result));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task ControlEndpoints_WhenUserIsNotOwnerNorAdmin_ReturnForbid(string action)
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Pending);
        var service = CreateService(context, userId: "stranger");

        var result = await InvokeAsync(service, action);

        Assert.IsType<ForbidHttpResult>(result);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task PauseAsync_WhenOwnerPausesPendingRepository_ReturnsSuccessAndPaused()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Pending);
        var service = CreateService(context, OwnerId);

        var result = await service.PauseAsync(CreateRequest());

        var ok = Assert.IsType<Ok<RegenerateResponse>>(result);
        Assert.True(ok.Value!.Success);
        Assert.Equal(RepositoryStatus.Paused, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task PauseAsync_WhenAdminPausesOtherUsersRepository_ReturnsSuccessAndPaused()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Processing);
        var service = CreateService(context, userId: "admin-1", isAdmin: true);

        var result = await service.PauseAsync(CreateRequest());

        Assert.Equal(StatusCodes.Status200OK, GetStatusCode(result));
        Assert.Equal(RepositoryStatus.Paused, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task PauseAsync_WhenRepositoryCompleted_ReturnsConflictWithMessage()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Completed);
        var service = CreateService(context, OwnerId);

        var result = await service.PauseAsync(CreateRequest());

        Assert.Equal(StatusCodes.Status409Conflict, GetStatusCode(result));
        Assert.False(string.IsNullOrWhiteSpace(GetErrorMessage(result)));
        Assert.Equal(RepositoryStatus.Completed, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task PauseAsync_WhenRepositoryAlreadyPaused_ReturnsConflict()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Paused);
        var service = CreateService(context, OwnerId);

        var result = await service.PauseAsync(CreateRequest());

        Assert.Equal(StatusCodes.Status409Conflict, GetStatusCode(result));
        Assert.Equal(RepositoryStatus.Paused, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task ResumeAsync_WhenPaused_ReturnsSuccessAndPending()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Paused);
        var service = CreateService(context, OwnerId);

        var result = await service.ResumeAsync(CreateRequest());

        var ok = Assert.IsType<Ok<RegenerateResponse>>(result);
        Assert.True(ok.Value!.Success);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task ResumeAsync_WhenPending_ReturnsConflict()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Pending);
        var service = CreateService(context, OwnerId);

        var result = await service.ResumeAsync(CreateRequest());

        Assert.Equal(StatusCodes.Status409Conflict, GetStatusCode(result));
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task StopAsync_WhenProcessing_ReturnsSuccessAndStopped()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Processing);
        var service = CreateService(context, OwnerId);

        var result = await service.StopAsync(CreateRequest());

        var ok = Assert.IsType<Ok<RegenerateResponse>>(result);
        Assert.True(ok.Value!.Success);
        Assert.Equal(RepositoryStatus.Stopped, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task StopAsync_WhenPaused_ReturnsSuccessAndStopped()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Paused);
        var service = CreateService(context, OwnerId);

        var result = await service.StopAsync(CreateRequest());

        Assert.Equal(StatusCodes.Status200OK, GetStatusCode(result));
        Assert.Equal(RepositoryStatus.Stopped, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task StopAsync_WhenCompleted_ReturnsConflict()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Completed);
        var service = CreateService(context, OwnerId);

        var result = await service.StopAsync(CreateRequest());

        Assert.Equal(StatusCodes.Status409Conflict, GetStatusCode(result));
        Assert.Equal(RepositoryStatus.Completed, await ReadStatusAsync(context));
    }

    [Fact]
    public async Task RestartAsync_WhenStopped_ReturnsSuccessAndPending()
    {
        using var context = CreateContext();
        await SeedRepositoryAsync(context, RepositoryStatus.Stopped);
        var service = CreateService(context, OwnerId);

        var result = await service.RestartAsync(CreateRequest());

        var ok = Assert.IsType<Ok<RegenerateResponse>>(result);
        Assert.True(ok.Value!.Success);
        Assert.Equal(RepositoryStatus.Pending, await ReadStatusAsync(context));
    }

    private static Task<IResult> InvokeAsync(RepositoryService service, string action)
    {
        return action switch
        {
            "pause" => service.PauseAsync(CreateRequest()),
            "resume" => service.ResumeAsync(CreateRequest()),
            "stop" => service.StopAsync(CreateRequest()),
            "restart" => service.RestartAsync(CreateRequest()),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private static RegenerateRequest CreateRequest()
    {
        return new RegenerateRequest
        {
            Owner = OrgName,
            Repo = RepoName
        };
    }

    private static int GetStatusCode(IResult result)
    {
        return Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;
    }

    private static string GetErrorMessage(IResult result)
    {
        var conflict = Assert.IsType<Conflict<RegenerateResponse>>(result);
        return conflict.Value!.ErrorMessage ?? string.Empty;
    }

    private static async Task SeedRepositoryAsync(TestDbContext context, RepositoryStatus status)
    {
        context.Users.Add(new User
        {
            Id = OwnerId,
            Name = "owner",
            Email = "owner@example.com"
        });
        await context.SaveChangesAsync();

        context.Repositories.Add(new Repository
        {
            Id = RepositoryId,
            OwnerUserId = OwnerId,
            GitUrl = $"https://github.com/{OrgName}/{RepoName}.git",
            OrgName = OrgName,
            RepoName = RepoName,
            Status = status
        });
        await context.SaveChangesAsync();
    }

    private static async Task<RepositoryStatus> ReadStatusAsync(TestDbContext context)
    {
        context.ChangeTracker.Clear();
        return await context.Repositories
            .AsNoTracking()
            .Where(item => item.Id == RepositoryId)
            .Select(item => item.Status)
            .SingleAsync();
    }

    private static RepositoryService CreateService(TestDbContext context, string? userId, bool isAdmin = false)
    {
        return new RepositoryService(
            context,
            Mock.Of<IGitPlatformService>(),
            new TestUserContext(userId, isAdmin),
            Mock.Of<IGitHubAppService>(),
            Mock.Of<IOrganizationService>(),
            new RepositoryFullRegenerationCleaner(),
            new RepositoryGenerationLockService(context),
            Options.Create(new RepositoryAnalyzerOptions()),
            new RepositoryProcessingControlService(new RepositoryFullRegenerationCleaner()));
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
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

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options)
    {
    }
}
