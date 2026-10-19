using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Localization;
using Shop.Core.Admin;
using Shop.Web.Areas.Admin.Models;

namespace Shop.Web.Areas.Admin.Controllers;

public class ProductsController : AdminControllerBase
{
    // The whole upload (up to 6 photos of 5 MB) plus the form fields.
    private const long MaxUploadRequest = ImageRules.MaxPerProduct * ImageRules.MaxBytes + 1_000_000;

    private readonly IAdminProductService _products;
    private readonly IAdminCategoryService _categories;
    private readonly IAdminAgeGroupService _ageGroups;
    private readonly IImageStore _images;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public ProductsController(
        IAdminProductService products,
        IAdminCategoryService categories,
        IAdminAgeGroupService ageGroups,
        IImageStore images,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _products = products;
        _categories = categories;
        _ageGroups = ageGroups;
        _images = images;
        _l = localizer;
    }

    [HttpGet]
    public async Task<IActionResult> Index(AdminProductFilter filter)
    {
        var result = await _products.ListAsync(filter, CurrentCulture.Code);
        return View(new AdminProductsViewModel(filter, result, await _categories.OptionsAsync(CurrentCulture.Code)));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int? id)
    {
        if (id is null)
        {
            var empty = new AdminProductViewModel();
            await FillOptionsAsync(empty);
            return View(empty);
        }

        var product = await _products.GetAsync(id.Value);
        if (product is null)
        {
            return NotFound();
        }

        var model = AdminProductViewModel.From(product.Form);
        await FillOptionsAsync(model);
        model.Images = product.Images;
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(AdminProductViewModel model)
    {
        if (!ModelState.IsValid)
        {
            await FillPageAsync(model);
            return View(model);
        }

        var result = await _products.SaveAsync(model.ToForm(), AdminId);

        switch (result.Status)
        {
            case SaveProductStatus.Saved:
                // A new product goes to its own page, where the photos can be added.
                if (model.Id is null)
                {
                    Success(_l["Admin_Msg_ProductCreated"].Value);
                    return RedirectToAction(nameof(Edit), new { id = result.Id });
                }

                Success(_l["Admin_Msg_Saved"].Value);
                return RedirectToAction(nameof(Edit), new { id = model.Id });

            case SaveProductStatus.NotFound:
                return NotFound();

            case SaveProductStatus.DuplicateSku:
                ModelState.AddModelError(nameof(AdminProductViewModel.Sku), _l["Admin_Msg_DuplicateSku"].Value);
                break;

            case SaveProductStatus.UnknownCategory:
                ModelState.AddModelError(nameof(AdminProductViewModel.CategoryId), _l.GetString("Val_Required", _l["Admin_Prod_Category"].Value).Value);
                break;

            default: // Conflict
                // The page is opened again with what is saved now, so the admin sees the new stock and decides again.
                Failure(_l["Admin_Msg_Conflict"].Value);
                return RedirectToAction(nameof(Edit), new { id = model.Id });
        }

        await FillPageAsync(model);
        return View(model);
    }

    // The quick "stock" box in the list.
    [HttpPost]
    public async Task<IActionResult> SetStock(int id, int stock, string? back)
    {
        if (stock is < 0 or > 100_000)
        {
            Failure(_l.GetString("Val_Range", _l["Admin_Prod_Stock"].Value, 0, 100000).Value);
        }
        else if (await _products.SetStockAsync(id, stock, AdminId))
        {
            Success(_l["Admin_Msg_Saved"].Value);
        }
        else
        {
            return NotFound();
        }

        return !string.IsNullOrEmpty(back) && Url.IsLocalUrl(back) ? LocalRedirect(back) : RedirectToAction(nameof(Index));
    }

    // ---- Photos ----

    [HttpPost]
    [RequestSizeLimit(MaxUploadRequest)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadRequest)]
    public async Task<IActionResult> UploadPhotos(int id, List<IFormFile> photos)
    {
        var files = photos.Where(f => f.Length > 0).ToList();
        if (files.Count == 0)
        {
            Failure(_l["Admin_Img_Choose"].Value);
            return RedirectToAction(nameof(Edit), new { id });
        }

        var existing = await _products.CountImagesAsync(id);
        if (existing + files.Count > ImageRules.MaxPerProduct)
        {
            Failure(_l.GetString("Admin_Img_TooMany", ImageRules.MaxPerProduct).Value);
            return RedirectToAction(nameof(Edit), new { id });
        }

        // All or nothing: if one file is refused, the ones already stored are deleted again.
        var stored = new List<StoredImage>();
        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var result = await _images.SaveAsync(stream, file.Length, $"products/{id}", makeThumb: true);

            if (result.Image is null)
            {
                DeleteStored(stored);
                Failure(_l.GetString("Admin_Img_Refused", Path.GetFileName(file.FileName), _l[result.ErrorKey!].Value).Value);
                return RedirectToAction(nameof(Edit), new { id });
            }

            stored.Add(result.Image);
        }

        var added = await _products.AddImagesAsync(id, stored, AdminId);
        if (added != ImageChangeStatus.Done)
        {
            DeleteStored(stored);

            if (added == ImageChangeStatus.NotFound)
            {
                return NotFound();
            }

            Failure(_l.GetString("Admin_Img_TooMany", ImageRules.MaxPerProduct).Value);
            return RedirectToAction(nameof(Edit), new { id });
        }

        Success(_l.GetString("Admin_Msg_PhotosAdded", stored.Count).Value);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> RemovePhoto(int id, int imageId)
    {
        var removed = await _products.RemoveImageAsync(id, imageId, AdminId);
        if (removed is null)
        {
            return NotFound();
        }

        // Only files in the uploads folder are deleted; the placeholder pictures of the sample products stay.
        _images.Delete(removed.Path, removed.ThumbPath);

        Success(_l["Admin_Msg_PhotoRemoved"].Value);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> MovePhoto(int id, int imageId, int direction)
    {
        var result = await _products.MoveImageAsync(id, imageId, direction, AdminId);
        return result == ImageChangeStatus.NotFound ? NotFound() : RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> MakeMainPhoto(int id, int imageId)
    {
        var result = await _products.MakeMainImageAsync(id, imageId, AdminId);
        return result == ImageChangeStatus.NotFound ? NotFound() : RedirectToAction(nameof(Edit), new { id });
    }

    // ---- helpers ----

    private void DeleteStored(IEnumerable<StoredImage> stored)
    {
        foreach (var image in stored)
        {
            _images.Delete(image.Path, image.ThumbPath);
        }
    }

    private async Task FillOptionsAsync(AdminProductViewModel model)
    {
        model.Categories = await _categories.OptionsAsync(CurrentCulture.Code);
        model.AgeGroups = await _ageGroups.OptionsAsync(CurrentCulture.Code);
    }

    // The lists around the form, when the form is shown again after a refused save.
    private async Task FillPageAsync(AdminProductViewModel model)
    {
        await FillOptionsAsync(model);

        if (model.Id is not null)
        {
            model.Images = (await _products.GetAsync(model.Id.Value))?.Images ?? Array.Empty<AdminProductImage>();
        }
    }
}
