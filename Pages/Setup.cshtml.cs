using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HirschNotify.Pages;

[AllowAnonymous]
public class SetupModel : PageModel
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;

    public SetupModel(UserManager<IdentityUser> userManager, SignInManager<IdentityUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    [BindProperty]
    public string ConfirmPassword { get; set; } = "";

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (_userManager.Users.Any())
            return RedirectToPage("Login");

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (_userManager.Users.Any())
            return RedirectToPage("Login");

        if (Password != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return Page();
        }

        var user = new IdentityUser { UserName = Username };
        var result = await _userManager.CreateAsync(user, Password);

        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: true);
            return RedirectToPage("Index");
        }

        ErrorMessage = string.Join(" ", result.Errors.Select(e => e.Description));
        return Page();
    }
}
