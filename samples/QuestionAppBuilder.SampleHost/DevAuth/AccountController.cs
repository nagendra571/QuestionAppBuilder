using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using QuestionAppBuilder.Editor.Options;
using QuestionAppBuilder.SampleHost.Integration;

namespace QuestionAppBuilder.SampleHost.DevAuth;

public sealed class LoginRequest
{
    [Required]
    public string Username { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public class AccountController : Controller
{
    private readonly DevUserStore _userStore;
    private readonly QuestionAppBuilderEditorOptions _editorOptions;

    public AccountController(DevUserStore userStore, QuestionAppBuilderEditorOptions editorOptions)
    {
        _userStore = userStore;
        _editorOptions = editorOptions;
    }

    [HttpGet]
    public IActionResult Login()
    {
        return View(new LoginRequest());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        if (!ModelState.IsValid || !_userStore.TryValidate(request.Username, request.Password, out var role))
        {
            ModelState.AddModelError(string.Empty, "Invalid username or password.");
            return View(request);
        }

        var identity = new ClaimsIdentity(
            new[]
            {
            new Claim(ClaimTypes.Name, request.Username),
                new Claim(ClaimTypes.Role, role),
            new Claim("iss", HostIdentityRegistration.DevelopmentIssuer),
            new Claim("sub", request.Username),
            },
            HostIdentityRegistration.CookieScheme);

        await HttpContext.SignInAsync(
            HostIdentityRegistration.CookieScheme,
            new ClaimsPrincipal(identity));

        return Redirect($"{_editorOptions.BasePath.TrimEnd('/')}/Applications");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(HostIdentityRegistration.CookieScheme);
        return RedirectToAction(nameof(Login));
    }
}
