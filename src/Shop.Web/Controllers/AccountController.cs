using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Shop.Core.Carts;
using Shop.Core.Entities;
using Shop.Core.Favorites;
using Shop.Core.Identity;
using Shop.Core.Phones;
using Shop.Web.Identity;
using Shop.Web.Models;

namespace Shop.Web.Controllers;

public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly AccountEmails _emails;
    private readonly ICartService _cart;
    private readonly IFavoriteService _favorites;
    private readonly GuestCookies _cookies;
    private readonly IHtmlLocalizer<SharedResource> _l;
    private readonly IWebHostEnvironment _environment;

    public AccountController(
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        AccountEmails emails,
        ICartService cart,
        IFavoriteService favorites,
        GuestCookies cookies,
        IHtmlLocalizer<SharedResource> localizer,
        IWebHostEnvironment environment)
    {
        _users = users;
        _signIn = signIn;
        _emails = emails;
        _cart = cart;
        _favorites = favorites;
        _cookies = cookies;
        _l = localizer;
        _environment = environment;
    }

    // ---- Register ----

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToAction("Index", "Home");
        }

        return View(new RegisterViewModel());
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // The phone was already checked by [ShopPhone].
        PhoneNumbers.TryNormalize(model.Phone, out var phone);

        var email = model.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FullName = NormalizeName(model.FullName),
            PhoneNumber = phone,
            CreatedAt = DateTime.UtcNow
        };

        var created = await _users.CreateAsync(user, model.Password);
        if (!created.Succeeded)
        {
            AddErrors(created);
            return View(model);
        }

        ThrowIfFailed(await _users.AddToRoleAsync(user, Roles.Customer), "add the new user to the Customer role");

        await SendConfirmationEmailAsync(user);

        return RedirectToAction(nameof(RegisterConfirmation), new { email });
    }

    [HttpGet]
    public async Task<IActionResult> RegisterConfirmation(string? email)
    {
        await OfferDevVerifyAsync(email);

        return Message(
            "Msg_CheckEmailTitle",
            "Msg_CheckEmailText",
            new object[] { email ?? string.Empty },
            new MessageLink(_l["Link_ResendConfirmation"].Value, Url.Action(nameof(ResendConfirmation), "Account")!));
    }

    // ---- Sign in / out ----

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _users.FindByEmailAsync(model.Email.Trim());
        if (user is null)
        {
            // Hash the password anyway, so the response time does not show whether the email exists.
            _users.PasswordHasher.HashPassword(new ApplicationUser(), model.Password);
            ModelState.AddModelError(string.Empty, _l["Login_Invalid"].Value);
            return View(model);
        }

        var result = await _signIn.CheckPasswordSignInAsync(user, model.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            ModelState.AddModelError(string.Empty, _l["Login_LockedOut"].Value);
            return View(model);
        }

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, _l["Login_Invalid"].Value);
            return View(model);
        }

        // The password is right. Only now can we say the email is not confirmed.
        if (!user.EmailConfirmed)
        {
            ModelState.AddModelError(string.Empty, _l["Login_NotConfirmed"].Value);
            model.ShowResend = true;
            await OfferDevVerifyAsync(user.Email);
            return View(model);
        }

        await _signIn.SignInAsync(user, isPersistent: model.RememberMe);
        await MoveGuestDataIntoAccountAsync(user);
        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    // ---- Email confirmation ----

    [HttpGet]
    public async Task<IActionResult> ConfirmEmail(string? userId, string? token)
    {
        var user = userId is null ? null : await _users.FindByIdAsync(userId);

        if (user is not null && TokenEncoding.TryDecode(token, out var decoded))
        {
            var result = await _users.ConfirmEmailAsync(user, decoded);
            if (result.Succeeded)
            {
                return Message(
                    "Msg_EmailConfirmedTitle",
                    "Msg_EmailConfirmedText",
                    null,
                    new MessageLink(_l["Nav_SignIn"].Value, Url.Action(nameof(Login), "Account")!));
            }
        }

        return Message(
            "Msg_LinkInvalidTitle",
            "Msg_LinkInvalidText",
            null,
            new MessageLink(_l["Link_ResendConfirmation"].Value, Url.Action(nameof(ResendConfirmation), "Account")!));
    }

    // Development only: confirms the account without an email (nothing is really sent in Development, the emails are files).
    // Outside Development this address does not exist, so it cannot be used to skip the email check on the real site.
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> DevVerify(string? email)
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        var user = string.IsNullOrWhiteSpace(email) ? null : await _users.FindByEmailAsync(email.Trim());

        if (user is not null && !user.EmailConfirmed)
        {
            var token = await _users.GenerateEmailConfirmationTokenAsync(user);
            ThrowIfFailed(await _users.ConfirmEmailAsync(user, token), "confirm the email in development");
        }

        if (user is null)
        {
            return Message("Msg_LinkInvalidTitle", "Msg_LinkInvalidText", null);
        }

        return Message(
            "Msg_EmailConfirmedTitle",
            "Msg_EmailConfirmedText",
            null,
            new MessageLink(_l["Nav_SignIn"].Value, Url.Action(nameof(Login), "Account")!));
    }

    [HttpGet]
    public IActionResult ResendConfirmation()
    {
        return View(new EmailOnlyViewModel());
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> ResendConfirmation(EmailOnlyViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _users.FindByEmailAsync(model.Email.Trim());
        if (user is not null && !user.EmailConfirmed)
        {
            await SendConfirmationEmailAsync(user);
        }

        // The same answer whether or not the email is registered.
        return Message("Msg_EmailSentTitle", "Msg_ResendSentText", null);
    }

    // ---- Forgotten password ----

    [HttpGet]
    public IActionResult ForgotPassword()
    {
        return View(new EmailOnlyViewModel());
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> ForgotPassword(EmailOnlyViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _users.FindByEmailAsync(model.Email.Trim());
        if (user is not null)
        {
            if (user.EmailConfirmed)
            {
                var token = await _users.GeneratePasswordResetTokenAsync(user);
                var url = Url.Action(nameof(ResetPassword), "Account", new { email = user.Email, token = TokenEncoding.Encode(token) }, Request.Scheme)!;
                await _emails.SendPasswordResetAsync(user, url);
            }
            else
            {
                await SendConfirmationEmailAsync(user);
            }
        }

        // The same answer whether or not the email is registered.
        return Message("Msg_EmailSentTitle", "Msg_ForgotSentText", null);
    }

    [HttpGet]
    public IActionResult ResetPassword(string? email, string? token)
    {
        if (string.IsNullOrEmpty(email) || !TokenEncoding.TryDecode(token, out _))
        {
            return Message("Msg_LinkInvalidTitle", "Msg_ResetLinkInvalidText", null, ForgotPasswordLink());
        }

        return View(new ResetPasswordViewModel { Email = email, Token = token! });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Account)]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        if (!TokenEncoding.TryDecode(model.Token, out var decoded))
        {
            return Message("Msg_LinkInvalidTitle", "Msg_ResetLinkInvalidText", null, ForgotPasswordLink());
        }

        var user = await _users.FindByEmailAsync(model.Email);
        if (user is null)
        {
            // Do not reveal that the account does not exist.
            return Message("Msg_LinkInvalidTitle", "Msg_ResetLinkInvalidText", null, ForgotPasswordLink());
        }

        var result = await _users.ResetPasswordAsync(user, decoded, model.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                return Message("Msg_LinkInvalidTitle", "Msg_ResetLinkInvalidText", null, ForgotPasswordLink());
            }

            AddErrors(result);
            return View(model);
        }

        // Receiving the link proves the email address is theirs.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            ThrowIfFailed(await _users.UpdateAsync(user), "confirm the email after a password reset");
        }

        return Message(
            "Msg_PasswordChangedTitle",
            "Msg_PasswordChangedText",
            null,
            new MessageLink(_l["Nav_SignIn"].Value, Url.Action(nameof(Login), "Account")!));
    }

    // ---- My account ----

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null)
        {
            return await SignOutDeletedUserAsync();
        }

        return View(new ProfileViewModel { FullName = user.FullName, Phone = user.PhoneNumber ?? string.Empty, Email = user.Email ?? string.Empty });
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> Index(ProfileViewModel model)
    {
        var user = await _users.GetUserAsync(User);
        if (user is null)
        {
            return await SignOutDeletedUserAsync();
        }

        model.Email = user.Email ?? string.Empty;

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        PhoneNumbers.TryNormalize(model.Phone, out var phone);
        user.FullName = NormalizeName(model.FullName);
        user.PhoneNumber = phone;

        ThrowIfFailed(await _users.UpdateAsync(user), "save the profile");

        // The name is kept in the sign-in cookie, so refresh it.
        await _signIn.RefreshSignInAsync(user);

        TempData["Saved"] = true;
        return RedirectToAction(nameof(Index));
    }

    [Authorize]
    [HttpGet]
    public IActionResult ChangePassword()
    {
        return View(new ChangePasswordViewModel());
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _users.GetUserAsync(User);
        if (user is null)
        {
            return await SignOutDeletedUserAsync();
        }

        var result = await _users.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(model);
        }

        await _signIn.RefreshSignInAsync(user);

        TempData["Saved"] = true;
        return RedirectToAction(nameof(ChangePassword));
    }

    // The old address of the orders tab: orders now live in OrdersController.
    [Authorize]
    [HttpGet]
    public IActionResult Orders()
    {
        return RedirectToAction("Index", "Orders");
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return Message(
            "Msg_AccessDeniedTitle",
            "Msg_AccessDeniedText",
            null,
            new MessageLink(_l["Back_Home"].Value, Url.Action("Index", "Home")!));
    }

    // ---- Helpers ----

    // What the visitor put in the cart and favorites before signing in joins the account, and the guest cookies are removed.
    private async Task MoveGuestDataIntoAccountAsync(ApplicationUser user)
    {
        if (_cookies.ReadCartId(Request) is Guid guestCartId)
        {
            await _cart.MergeGuestCartAsync(guestCartId, user.Id);
            _cookies.ClearCart(Request, Response);
        }

        var guestFavorites = _cookies.ReadFavorites(Request);
        if (guestFavorites.Count > 0)
        {
            await _favorites.AddManyAsync(user.Id, guestFavorites);
            _cookies.ClearFavorites(Request, Response);
        }
    }

    // Development only: asks the page to show the "Verify my account" button, for an account that exists and is not confirmed yet.
    private async Task OfferDevVerifyAsync(string? email)
    {
        if (!_environment.IsDevelopment() || string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        var user = await _users.FindByEmailAsync(email.Trim());
        if (user is not null && !user.EmailConfirmed)
        {
            ViewData["DevVerifyEmail"] = user.Email;
        }
    }

    private async Task SendConfirmationEmailAsync(ApplicationUser user)
    {
        var token = await _users.GenerateEmailConfirmationTokenAsync(user);
        var url = Url.Action(nameof(ConfirmEmail), "Account", new { userId = user.Id, token = TokenEncoding.Encode(token) }, Request.Scheme)!;
        await _emails.SendConfirmationAsync(user, url);
    }

    // Only local addresses are followed, so a crafted link cannot send the visitor to another site.
    private IActionResult RedirectToLocal(string? returnUrl)
    {
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToAction("Index", "Home");
    }

    private async Task<IActionResult> SignOutDeletedUserAsync()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    private IActionResult Message(string titleKey, string textKey, object[]? textArguments, params MessageLink[] links)
    {
        var text = textArguments is null ? _l[textKey].Value : _l.GetString(textKey, textArguments).Value;

        return View("Message", new MessageViewModel
        {
            Title = _l[titleKey].Value,
            Text = text,
            Links = links
        });
    }

    private MessageLink ForgotPasswordLink()
    {
        return new MessageLink(_l["Link_ForgotPassword"].Value, Url.Action(nameof(ForgotPassword), "Account")!);
    }

    // Identity errors go next to the field they are about, or in the summary at the top.
    private void AddErrors(IdentityResult result)
    {
        foreach (var error in result.Errors)
        {
            var field = error.Code switch
            {
                "DuplicateEmail" or "InvalidEmail" => "Email",
                "PasswordMismatch" => "CurrentPassword",
                var code when code.StartsWith("Password") => "Password",
                _ => string.Empty
            };

            // The change-password form calls its new-password field NewPassword.
            if (field == "Password" && ModelState.ContainsKey("NewPassword"))
            {
                field = "NewPassword";
            }

            ModelState.AddModelError(field, error.Description);
        }
    }

    private static string NormalizeName(string name)
    {
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static void ThrowIfFailed(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not {action}: " + string.Join("; ", result.Errors.Select(e => $"{e.Code} {e.Description}")));
        }
    }
}
