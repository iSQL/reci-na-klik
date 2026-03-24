using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReciNaKlik.Application.Features.Audit;
using ReciNaKlik.Application.Features.Authentication;
using ReciNaKlik.Application.Features.Authentication.Dtos;
using ReciNaKlik.Application.Features.Email;
using ReciNaKlik.Application.Features.Email.Models;
using ReciNaKlik.Application.Identity;
using ReciNaKlik.Application.Identity.Constants;
using ReciNaKlik.Infrastructure.Cryptography;
using ReciNaKlik.Infrastructure.Features.Authentication.Models;
using ReciNaKlik.Infrastructure.Features.Authentication.Options;
using ReciNaKlik.Infrastructure.Features.Email.Options;
using ReciNaKlik.Infrastructure.Persistence;
using ReciNaKlik.Shared;

namespace ReciNaKlik.Infrastructure.Features.Authentication.Services;

/// <summary>
/// Identity-backed implementation of <see cref="IAuthenticationService"/> with JWT token rotation.
/// </summary>
internal class AuthenticationService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    IUserContext userContext,
    ITemplatedEmailSender templatedEmailSender,
    EmailTokenService emailTokenService,
    IAuditService auditService,
    ITokenSessionService tokenSessionService,
    IOptions<AuthenticationOptions> authenticationOptions,
    IOptions<EmailOptions> emailOptions,
    ILogger<AuthenticationService> logger,
    ReciNaKlikDbContext dbContext) : IAuthenticationService
{
    private readonly AuthenticationOptions.EmailTokenOptions _emailTokenOptions = authenticationOptions.Value.EmailToken;
    private readonly EmailOptions _emailOptions = emailOptions.Value;

    /// <inheritdoc />
    public async Task<Result<LoginOutput>> Login(string username, string password, bool useCookies = false, bool rememberMe = false, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByNameAsync(username);

        if (user is null)
        {
            await auditService.LogAsync(AuditActions.LoginFailure,
                metadata: JsonSerializer.Serialize(new { attemptedEmail = username }),
                ct: cancellationToken);
            return Result<LoginOutput>.Failure(ErrorMessages.Auth.LoginInvalidCredentials, ErrorType.Unauthorized);
        }

        var signInResult = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (signInResult.IsLockedOut)
        {
            await auditService.LogAsync(AuditActions.LoginFailure, userId: user.Id, ct: cancellationToken);
            return Result<LoginOutput>.Failure(ErrorMessages.Auth.LoginAccountLocked, ErrorType.Unauthorized);
        }

        if (!signInResult.Succeeded)
        {
            await auditService.LogAsync(AuditActions.LoginFailure, userId: user.Id, ct: cancellationToken);
            return Result<LoginOutput>.Failure(ErrorMessages.Auth.LoginInvalidCredentials, ErrorType.Unauthorized);
        }

        var tokens = await tokenSessionService.GenerateTokensAsync(user, useCookies, rememberMe, cancellationToken);
        await auditService.LogAsync(AuditActions.LoginSuccess, userId: user.Id, ct: cancellationToken);

        return Result<LoginOutput>.Success(new LoginOutput(
            Tokens: tokens,
            ChallengeToken: null,
            RequiresTwoFactor: false));
    }


    /// <inheritdoc />
    public async Task<Result<Guid>> Register(RegisterInput input, CancellationToken cancellationToken = default)
    {
        var normalizedPhone = PhoneNumberHelper.Normalize(input.PhoneNumber);

        if (normalizedPhone is not null && await IsPhoneNumberTakenAsync(normalizedPhone, excludeUserId: null))
        {
            return Result<Guid>.Failure(ErrorMessages.User.PhoneNumberTaken);
        }

        var user = new ApplicationUser
        {
            UserName = input.Email,
            Email = input.Email,
            FirstName = input.FirstName,
            LastName = input.LastName,
            PhoneNumber = normalizedPhone
        };

        var result = await userManager.CreateAsync(user, input.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return Result<Guid>.Failure(errors);
        }

        var roleResult = await userManager.AddToRoleAsync(user, AppRoles.User);

        if (!roleResult.Succeeded)
        {
            return Result<Guid>.Failure(ErrorMessages.Auth.RegisterRoleAssignFailed);
        }

        await SendVerificationEmailAsync(user, cancellationToken);

        await auditService.LogAsync(AuditActions.Register, userId: user.Id, ct: cancellationToken);

        return Result<Guid>.Success(user.Id);
    }

    /// <inheritdoc />
    public async Task Logout(CancellationToken cancellationToken = default)
    {
        // Get user ID before clearing cookies
        var userId = userContext.UserId;

        tokenSessionService.DeleteAuthCookies();

        if (userId.HasValue)
        {
            await auditService.LogAsync(AuditActions.Logout, userId: userId.Value, ct: cancellationToken);
            await tokenSessionService.RevokeUserTokensAsync(userId.Value, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<Result<AuthenticationOutput>> RefreshTokenAsync(
        string refreshToken, bool useCookies = false, CancellationToken cancellationToken = default)
        => tokenSessionService.RefreshTokenAsync(refreshToken, useCookies, cancellationToken);

    /// <inheritdoc />
    public async Task<Result> ChangePasswordAsync(ChangePasswordInput input, CancellationToken cancellationToken = default)
    {
        var userId = userContext.UserId;

        if (!userId.HasValue)
        {
            return Result.Failure(ErrorMessages.Auth.NotAuthenticated, ErrorType.Unauthorized);
        }

        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        if (user is null)
        {
            return Result.Failure(ErrorMessages.Auth.UserNotFound);
        }

        var passwordValid = await userManager.CheckPasswordAsync(user, input.CurrentPassword);

        if (!passwordValid)
        {
            return Result.Failure(ErrorMessages.Auth.PasswordIncorrect);
        }

        var changeResult = await userManager.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);

        if (!changeResult.Succeeded)
        {
            var errors = string.Join(", ", changeResult.Errors.Select(e => e.Description));
            return Result.Failure(errors);
        }

        await tokenSessionService.RevokeUserTokensAsync(userId.Value, cancellationToken);

        await auditService.LogAsync(AuditActions.PasswordChange, userId: userId.Value, ct: cancellationToken);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            // Return success to prevent user enumeration
            logger.LogDebug("Forgot password requested for non-existent email {Email}", email);
            return Result.Success();
        }

        var identityToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var opaqueToken = await emailTokenService.CreateAsync(user.Id, identityToken, EmailTokenPurpose.PasswordReset, cancellationToken);
        var resetUrl = $"{_emailOptions.FrontendBaseUrl.TrimEnd('/')}/reset-password?token={opaqueToken}";

        var model = new ResetPasswordModel(resetUrl, _emailTokenOptions.Lifetime.ToHumanReadable());
        await templatedEmailSender.SendSafeAsync(EmailTemplateNames.ResetPassword, model, email, cancellationToken);

        await auditService.LogAsync(AuditActions.PasswordResetRequest, userId: user.Id, ct: cancellationToken);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ResetPasswordAsync(ResetPasswordInput input, CancellationToken cancellationToken = default)
    {
        var emailToken = await emailTokenService.ResolveAsync(input.Token, EmailTokenPurpose.PasswordReset, cancellationToken);

        if (emailToken is null)
        {
            return Result.Failure(ErrorMessages.Auth.ResetPasswordFailed);
        }

        var user = await userManager.FindByIdAsync(emailToken.UserId.ToString());

        if (user is null)
        {
            return Result.Failure(ErrorMessages.Auth.ResetPasswordFailed);
        }

        if (await userManager.CheckPasswordAsync(user, input.NewPassword))
        {
            return Result.Failure(ErrorMessages.Auth.PasswordSameAsCurrent);
        }

        var resetResult = await userManager.ResetPasswordAsync(user, emailToken.IdentityToken, input.NewPassword);

        if (!resetResult.Succeeded)
        {
            var errors = resetResult.Errors.Select(e => e.Description).ToList();

            // Distinguish between invalid token and other Identity errors (e.g., password policy)
            if (errors.Any(e => e.Contains("Invalid token", StringComparison.OrdinalIgnoreCase)))
            {
                return Result.Failure(ErrorMessages.Auth.ResetPasswordTokenInvalid);
            }

            return Result.Failure(string.Join(" ", errors));
        }

        emailToken.IsUsed = true;
        await dbContext.SaveChangesAsync(cancellationToken);

        await tokenSessionService.RevokeUserTokensAsync(user.Id, cancellationToken);

        await auditService.LogAsync(AuditActions.PasswordReset, userId: user.Id, ct: cancellationToken);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> VerifyEmailAsync(VerifyEmailInput input, CancellationToken cancellationToken = default)
    {
        var emailToken = await emailTokenService.ResolveAsync(input.Token, EmailTokenPurpose.EmailVerification, cancellationToken);

        if (emailToken is null)
        {
            return Result.Failure(ErrorMessages.Auth.EmailVerificationFailed);
        }

        var user = await userManager.FindByIdAsync(emailToken.UserId.ToString());

        if (user is null)
        {
            return Result.Failure(ErrorMessages.Auth.EmailVerificationFailed);
        }

        if (user.EmailConfirmed)
        {
            return Result.Failure(ErrorMessages.Auth.EmailAlreadyVerified);
        }

        var confirmResult = await userManager.ConfirmEmailAsync(user, emailToken.IdentityToken);

        if (!confirmResult.Succeeded)
        {
            return Result.Failure(ErrorMessages.Auth.EmailVerificationFailed);
        }

        emailToken.IsUsed = true;
        await dbContext.SaveChangesAsync(cancellationToken);

        await auditService.LogAsync(AuditActions.EmailVerification, userId: user.Id, ct: cancellationToken);

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> ResendVerificationEmailAsync(CancellationToken cancellationToken = default)
    {
        var userId = userContext.UserId;

        if (!userId.HasValue)
        {
            return Result.Failure(ErrorMessages.Auth.NotAuthenticated, ErrorType.Unauthorized);
        }

        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        if (user is null)
        {
            return Result.Failure(ErrorMessages.Auth.UserNotFound);
        }

        if (user.EmailConfirmed)
        {
            return Result.Failure(ErrorMessages.Auth.EmailAlreadyVerified);
        }

        await SendVerificationEmailAsync(user, cancellationToken);

        await auditService.LogAsync(AuditActions.ResendVerificationEmail, userId: userId.Value, ct: cancellationToken);

        return Result.Success();
    }


    /// <summary>
    /// Generates a cryptographically random challenge token as a base64 string.
    /// </summary>
    private static string GenerateChallengeToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Sends a verification email to the specified user with a confirmation link.
    /// </summary>
    private async Task SendVerificationEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            logger.LogWarning("Cannot send verification email: user {UserId} has no email address", user.Id);
            return;
        }

        var identityToken = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var opaqueToken = await emailTokenService.CreateAsync(user.Id, identityToken, EmailTokenPurpose.EmailVerification, cancellationToken);
        var verifyUrl = $"{_emailOptions.FrontendBaseUrl.TrimEnd('/')}/verify-email?token={opaqueToken}";

        var model = new VerifyEmailModel(verifyUrl);
        await templatedEmailSender.SendSafeAsync(EmailTemplateNames.VerifyEmail, model, user.Email, cancellationToken);
    }

    /// <summary>
    /// Checks whether any existing user already has the given normalized phone number.
    /// </summary>
    private async Task<bool> IsPhoneNumberTakenAsync(string normalizedPhone, Guid? excludeUserId)
    {
        return await userManager.Users
            .AnyAsync(u =>
                u.PhoneNumber != null
                && u.PhoneNumber == normalizedPhone
                && (!excludeUserId.HasValue || u.Id != excludeUserId.Value));
    }
}
