using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EventAlertService.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly SignInManager<IdentityUser> _signInManager;
    private readonly UserManager<IdentityUser> _userManager;

    public LoginModel(SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // If no users exist, redirect to setup
        if (!_userManager.Users.Any())
            return RedirectToPage("Setup");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            ErrorMessage = "Please fill in all fields.";
            return Page();
        }

        var result = await _signInManager.PasswordSignInAsync(Username, Password, isPersistent: true, lockoutOnFailure: false);

        if (result.Succeeded)
            return RedirectToPage("Index");

        ErrorMessage = "Invalid username or password.";
        return Page();
    }

    public async Task<IActionResult> OnGetLogoutAsync()
    {
        await _signInManager.SignOutAsync();
        return RedirectToPage("Login");
    }
}
