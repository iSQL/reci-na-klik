using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using ReciNaKlik.Application.Caching.Constants;
using ReciNaKlik.Infrastructure.Caching.Services;
using ReciNaKlik.Application.Cookies;
using ReciNaKlik.Application.Cookies.Constants;
using ReciNaKlik.Application.Features.Audit;
using ReciNaKlik.Application.Features.Authentication.Dtos;
using ReciNaKlik.Application.Identity;
using ReciNaKlik.Application.Identity.Constants;
using ReciNaKlik.Application.Identity.Dtos;
using ReciNaKlik.Component.Tests.Fixtures;
using ReciNaKlik.Infrastructure.Features.Authentication.Models;
using ReciNaKlik.Infrastructure.Identity.Services;
using ReciNaKlik.Infrastructure.Persistence;
using ReciNaKlik.Shared;

namespace ReciNaKlik.Component.Tests.Services;

public class UserServiceTests : IDisposable
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<ApplicationRole> _roleManager;
    private readonly IUserContext _userContext;
    private readonly HybridCache _hybridCache;
    private readonly ICookieService _cookieService;
    private readonly IAuditService _auditService;
    private readonly ReciNaKlikDbContext _dbContext;
    private readonly UserService _sut;

    private readonly Guid _userId = Guid.NewGuid();

    public UserServiceTests()
    {
        _userManager = IdentityMockHelpers.CreateMockUserManager();
        _roleManager = IdentityMockHelpers.CreateMockRoleManager();
        _userContext = Substitute.For<IUserContext>();
        _hybridCache = Substitute.ForPartsOf<NoOpHybridCache>();
        _cookieService = Substitute.For<ICookieService>();
        _auditService = Substitute.For<IAuditService>();
        _dbContext = TestDbContextFactory.Create();

        _sut = new UserService(
            _userManager, _roleManager, _userContext, _hybridCache, _dbContext, _cookieService,
            _auditService,
            Substitute.For<ILogger<UserService>>());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _userManager.Dispose();
    }

    #region GetCurrentUser

    [Fact]
    public async Task GetCurrentUser_Authenticated_ReturnsUserData()
    {
        var user = new ApplicationUser
        {
            Id = _userId,
            UserName = "test@example.com",
            FirstName = "John",
            LastName = "Doe"
        };
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.GetRolesAsync(user).Returns(new List<string> { "User" });

        var result = await _sut.GetCurrentUserAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("test@example.com", result.Value.UserName);
        Assert.Equal("John", result.Value.FirstName);
    }

    [Fact]
    public async Task GetCurrentUser_NotAuthenticated_ReturnsUnauthorized()
    {
        _userContext.UserId.Returns((Guid?)null);

        var result = await _sut.GetCurrentUserAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.NotAuthenticated, result.Error);
        Assert.Equal(ErrorType.Unauthorized, result.ErrorType);
    }

    [Fact]
    public async Task GetCurrentUser_UserNotFound_ReturnsFailure()
    {
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns((ApplicationUser?)null);

        var result = await _sut.GetCurrentUserAsync();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.NotFound, result.Error);
    }

    #endregion

    #region UpdateProfile

    [Fact]
    public async Task UpdateProfile_Valid_ReturnsUpdatedUser()
    {
        var user = new ApplicationUser
        {
            Id = _userId,
            UserName = "test@example.com"
        };
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.UpdateAsync(user).Returns(IdentityResult.Success);
        _userManager.GetRolesAsync(user).Returns(new List<string> { "User" });

        var result = await _sut.UpdateProfileAsync(
            new UpdateProfileInput("Jane", "Doe", null, "Bio text"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Jane", result.Value.FirstName);
        Assert.Equal("Doe", result.Value.LastName);
        await _auditService.Received(1).LogAsync(
            AuditActions.ProfileUpdate,
            userId: _userId,
            targetEntityType: Arg.Any<string?>(),
            targetEntityId: Arg.Any<Guid?>(),
            metadata: Arg.Any<string?>(),
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateProfile_DuplicatePhone_ReturnsFailure()
    {
        _dbContext.Users.Add(new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "other@example.com",
            PhoneNumber = "+420123456789"
        });
        await _dbContext.SaveChangesAsync();

        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString())
            .Returns(new ApplicationUser { Id = _userId, UserName = "test@example.com" });
        _userManager.Users.Returns(_dbContext.Users);

        var result = await _sut.UpdateProfileAsync(
            new UpdateProfileInput(null, null, "+420123456789", null));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.PhoneNumberTaken, result.Error);
    }

    [Fact]
    public async Task UpdateProfile_NotAuthenticated_ReturnsUnauthorized()
    {
        _userContext.UserId.Returns((Guid?)null);

        var result = await _sut.UpdateProfileAsync(
            new UpdateProfileInput("Jane", null, null, null));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.NotAuthenticated, result.Error);
        Assert.Equal(ErrorType.Unauthorized, result.ErrorType);
    }

    [Fact]
    public async Task UpdateProfile_UserNotFound_ReturnsFailure()
    {
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns((ApplicationUser?)null);

        var result = await _sut.UpdateProfileAsync(
            new UpdateProfileInput("Jane", null, null, null));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.NotFound, result.Error);
    }

    #endregion

    #region DeleteAccount

    [Fact]
    public async Task DeleteAccount_Valid_ReturnsSuccess()
    {
        var user = new ApplicationUser { Id = _userId, UserName = "test@example.com" };
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.CheckPasswordAsync(user, "correct").Returns(true);
        _userManager.GetRolesAsync(user).Returns(new List<string> { "User" });
        _userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        var result = await _sut.DeleteAccountAsync(new DeleteAccountInput("correct"));

        Assert.True(result.IsSuccess);
        await _auditService.Received(1).LogAsync(
            AuditActions.AccountDeletion,
            userId: _userId,
            targetEntityType: Arg.Any<string?>(),
            targetEntityId: Arg.Any<Guid?>(),
            metadata: Arg.Any<string?>(),
            ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAccount_WrongPassword_ReturnsFailure()
    {
        _userContext.UserId.Returns(_userId);
        var user = new ApplicationUser { Id = _userId, UserName = "test@example.com" };
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.CheckPasswordAsync(user, "wrong").Returns(false);

        var result = await _sut.DeleteAccountAsync(new DeleteAccountInput("wrong"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.DeleteInvalidPassword, result.Error);
    }

    [Fact]
    public async Task DeleteAccount_NotAuthenticated_ReturnsUnauthorized()
    {
        _userContext.UserId.Returns((Guid?)null);

        var result = await _sut.DeleteAccountAsync(new DeleteAccountInput("password"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.NotAuthenticated, result.Error);
        Assert.Equal(ErrorType.Unauthorized, result.ErrorType);
    }

    [Fact]
    public async Task DeleteAccount_LastSuperuser_ReturnsFailure()
    {
        var user = new ApplicationUser { Id = _userId, UserName = "superuser@example.com" };
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.CheckPasswordAsync(user, "correct").Returns(true);
        _userManager.GetRolesAsync(user).Returns(new List<string> { AppRoles.Superuser });

        // Set up single Superuser in role
        var superAdminRole = new ApplicationRole { Id = Guid.NewGuid(), Name = AppRoles.Superuser };
        _roleManager.FindByNameAsync(AppRoles.Superuser).Returns(superAdminRole);
        _dbContext.UserRoles.Add(new IdentityUserRole<Guid> { RoleId = superAdminRole.Id, UserId = _userId });
        await _dbContext.SaveChangesAsync();

        var result = await _sut.DeleteAccountAsync(new DeleteAccountInput("correct"));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorMessages.User.LastSuperuserCannotDelete, result.Error);
    }

    [Fact]
    public async Task DeleteAccount_Valid_RevokesTokensAndClearsState()
    {
        var user = new ApplicationUser { Id = _userId, UserName = "test@example.com" };
        _userContext.UserId.Returns(_userId);
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.CheckPasswordAsync(user, "correct").Returns(true);
        _userManager.GetRolesAsync(user).Returns(new List<string> { AppRoles.User });
        _userManager.DeleteAsync(user).Returns(IdentityResult.Success);

        // Seed a refresh token
        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = "hashed-token",
            UserId = _userId,
            CreatedAt = DateTime.UtcNow,
            ExpiredAt = DateTime.UtcNow.AddDays(7),
            IsUsed = false,
            IsInvalidated = false
        });
        await _dbContext.SaveChangesAsync();

        var result = await _sut.DeleteAccountAsync(new DeleteAccountInput("correct"));

        Assert.True(result.IsSuccess);

        // Verify refresh tokens were invalidated
        var token = Assert.Single(_dbContext.RefreshTokens);
        Assert.True(token.IsInvalidated);

        // Verify cookies were cleared
        _cookieService.Received(1).DeleteCookie(CookieNames.AccessToken);
        _cookieService.Received(1).DeleteCookie(CookieNames.RefreshToken);

        // Verify cache was invalidated
        await _hybridCache.Received(1).RemoveAsync(CacheKeys.User(_userId));
    }

    #endregion

    #region GetUserRoles

    [Fact]
    public async Task GetUserRoles_UserNotFound_ReturnsEmptyList()
    {
        _userManager.FindByIdAsync(Arg.Any<string>()).Returns((ApplicationUser?)null);

        var roles = await _sut.GetUserRolesAsync(Guid.NewGuid());

        Assert.Empty(roles);
    }

    [Fact]
    public async Task GetUserRoles_UserExists_ReturnsRoles()
    {
        var user = new ApplicationUser { Id = _userId };
        _userManager.FindByIdAsync(_userId.ToString()).Returns(user);
        _userManager.GetRolesAsync(user).Returns(new List<string> { "User", "Admin" });

        var roles = await _sut.GetUserRolesAsync(_userId);

        Assert.Equal(2, roles.Count);
        Assert.Contains("User", roles);
        Assert.Contains("Admin", roles);
    }

    #endregion




}
