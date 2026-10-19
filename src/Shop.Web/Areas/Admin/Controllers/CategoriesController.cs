using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class CategoriesController : AdminControllerBase
{
    private readonly IAdminCategoryService _categories;
    private readonly IImageStore _images;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public CategoriesController(IAdminCategoryService categories, IImageStore images, IHtmlLocalizer<SharedResource> localizer)
    {
        _categories = categories;
        _images = images;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        return View(await _categories.ListAsync(CurrentCulture.Code));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int? id)
    {
        if (id is null)
        {
            return View(new AdminCategoryViewModel { SortOrder = (await _categories.ListAsync(CurrentCulture.Code)).Count + 1 });
        }

        var form = await _categories.GetAsync(id.Value);
        if (form is null)
        {
            return NotFound();
        }

        return View(new AdminCategoryViewModel
        {
            Id = form.Id,
            NameHy = form.NameHy,
            NameRu = form.NameRu,
            NameEn = form.NameEn,
            SortOrder = form.SortOrder,
            IsActive = form.IsActive,
            ImagePath = form.ImagePath
        });
    }

    [HttpPost]
    [RequestSizeLimit(ImageRules.MaxBytes + 1_000_000)]
    public async Task<IActionResult> Edit(AdminCategoryViewModel model)
    {
        // The picture is checked first: a refused file is a form error like any other.
        StoredImage? stored = null;
        if (model.Image is { Length: > 0 } file)
        {
            await using var stream = file.OpenReadStream();
            var result = await _images.SaveAsync(stream, file.Length, "categories", makeThumb: false);

            if (result.Image is null)
            {
                ModelState.AddModelError(nameof(AdminCategoryViewModel.Image), _l[result.ErrorKey!].Value);
            }
            else
            {
                stored = result.Image;
            }
        }

        if (!ModelState.IsValid)
        {
            // Nothing is saved, so a picture that was stored for this attempt must not stay on the disk.
            _images.Delete(stored?.Path);
            return View(model);
        }

        var saved = await _categories.SaveAsync(new AdminCategoryForm
        {
            Id = model.Id,
            NameHy = model.NameHy,
            NameRu = model.NameRu ?? string.Empty,
            NameEn = model.NameEn ?? string.Empty,
            SortOrder = model.SortOrder,
            IsActive = model.IsActive,
            ImagePath = stored?.Path ?? model.ImagePath
        }, AdminId);

        if (saved.Id is null)
        {
            _images.Delete(stored?.Path);
            return NotFound();
        }

        // The old picture is no longer used.
        _images.Delete(saved.ReplacedImagePath);

        Success(_l["Admin_Msg_Saved"].Value);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _categories.DeleteAsync(id, AdminId);

        switch (result.Status)
        {
            case DeleteStatus.Deleted:
                _images.Delete(result.ImagePath);
                Success(_l["Admin_Msg_Deleted"].Value);
                break;
            case DeleteStatus.NotFound:
                return NotFound();
            default:
                Failure(_l["Admin_Msg_CategoryInUse"].Value);
                break;
        }

        return RedirectToAction(nameof(Index));
    }
}
