using ReciNaKlik.Application.Features.Authentication.Dtos;
using ReciNaKlik.WebApi.Features.Authentication.Dtos.ChangePassword;
using ReciNaKlik.WebApi.Features.Authentication.Dtos.Login;
using ReciNaKlik.WebApi.Features.Authentication.Dtos.Register;
using ReciNaKlik.WebApi.Features.Authentication.Dtos.ResetPassword;
using ReciNaKlik.WebApi.Features.Authentication.Dtos.SetPassword;
using ReciNaKlik.WebApi.Features.Authentication.Dtos.VerifyEmail;

namespace ReciNaKlik.WebApi.Features.Authentication;

/// <summary>
/// Maps between authentication WebApi DTOs and Application layer DTOs.
/// </summary>
internal static class AuthMapper
{
    /// <summary>
    /// Maps a <see cref="RegisterRequest"/> to a <see cref="RegisterInput"/>.
    /// </summary>
    public static RegisterInput ToRegisterInput(this RegisterRequest request) =>
        new(
            Email: request.Email,
            Password: request.Password,
            FirstName: request.FirstName,
            LastName: request.LastName,
            PhoneNumber: request.PhoneNumber
        );

    /// <summary>
    /// Maps an <see cref="AuthenticationOutput"/> to an <see cref="AuthenticationResponse"/>.
    /// </summary>
    public static AuthenticationResponse ToResponse(this AuthenticationOutput output) =>
        new()
        {
            AccessToken = output.AccessToken,
            RefreshToken = output.RefreshToken
        };

    /// <summary>
    /// Maps a <see cref="LoginOutput"/> to an <see cref="AuthenticationResponse"/>.
    /// When 2FA is required, tokens are empty and challenge fields are populated.
    /// </summary>
    public static AuthenticationResponse ToResponse(this LoginOutput output) =>
        output.Tokens is { } tokens
            ? new AuthenticationResponse
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken
            }
            : new AuthenticationResponse
            {
                RequiresTwoFactor = true,
                ChallengeToken = output.ChallengeToken
            };

    /// <summary>
    /// Maps a <see cref="ChangePasswordRequest"/> to a <see cref="ChangePasswordInput"/>.
    /// </summary>
    public static ChangePasswordInput ToChangePasswordInput(this ChangePasswordRequest request) =>
        new(
            CurrentPassword: request.CurrentPassword,
            NewPassword: request.NewPassword
        );

    /// <summary>
    /// Maps a <see cref="ResetPasswordRequest"/> to a <see cref="ResetPasswordInput"/>.
    /// </summary>
    public static ResetPasswordInput ToResetPasswordInput(this ResetPasswordRequest request) =>
        new(
            Token: request.Token,
            NewPassword: request.NewPassword
        );

    /// <summary>
    /// Maps a <see cref="VerifyEmailRequest"/> to a <see cref="VerifyEmailInput"/>.
    /// </summary>
    public static VerifyEmailInput ToVerifyEmailInput(this VerifyEmailRequest request) =>
        new(
            Token: request.Token
        );


}
