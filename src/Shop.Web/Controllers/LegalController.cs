using Microsoft.AspNetCore.Mvc;
using Shop.Web.Legal;

namespace Shop.Web.Controllers;

// The delivery and returns page and the terms of use (the privacy page is Home/Privacy).
public class LegalController : Controller
{
    private readonly LegalPages _legal;

    public LegalController(LegalPages legal)
    {
        _legal = legal;
    }

    public async Task<IActionResult> Delivery() => View(await _legal.BuildAsync());

    public async Task<IActionResult> Terms() => View(await _legal.BuildAsync());
}
