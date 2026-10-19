using System.Text;
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
    private readonly IAdminProductToolsService _tools;
    private readonly IAdminCategoryService _categories;
    private readonly IAdminAgeGroupService _ageGroups;
    private readonly IImageStore _images;
    private readonly IHtmlLocalizer<SharedResource> _l;

    public ProductsController(
        IAdminProductService products,
        IAdminProductToolsService tools,
        IAdminCategoryService categories,
        IAdminAgeGroupService ageGroups,
        IImageStore images,
        IHtmlLocalizer<SharedResource> localizer)
    {
        _products = products;
        _tools = tools;
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

    // ---- Duplicate ----

    [HttpPost]
    public async Task<IActionResult> Duplicate(int id, string? sku)
    {
        var result = await _tools.DuplicateAsync(id, sku ?? string.Empty, AdminId);

        switch (result.Status)
        {
            case DuplicateProductStatus.NotFound:
                return NotFound();
            case DuplicateProductStatus.InvalidSku:
                Failure(_l["Admin_Dup_BadSku"].Value);
                return RedirectToAction(nameof(Edit), new { id });
            case DuplicateProductStatus.DuplicateSku:
                Failure(_l["Admin_Dup_TakenSku"].Value);
                return RedirectToAction(nameof(Edit), new { id });
        }

        // The photos are files: each one is copied, so deleting a photo of the copy never touches the original.
        var newId = result.NewId!.Value;
        var source = await _products.GetAsync(id);
        var wanted = source?.Images.OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToList() ?? [];
        var copied = new List<StoredImage>();

        foreach (var image in wanted)
        {
            var copy = await _images.CopyAsync(new StoredImage(image.Path, image.ThumbPath), $"products/{newId}");
            if (copy is not null)
            {
                copied.Add(copy);
            }
        }

        if (copied.Count > 0)
        {
            var added = await _products.AddImagesAsync(newId, copied, AdminId);
            if (added != ImageChangeStatus.Done)
            {
                DeleteStored(copied);
                throw new InvalidOperationException($"The photos of the copy of product {id} could not be added: {added}.");
            }
        }

        Success(_l.GetString("Admin_Dup_Done", copied.Count, wanted.Count).Value);
        return RedirectToAction(nameof(Edit), new { id = newId });
    }

    // ---- Change many products ----

    [HttpPost]
    public async Task<IActionResult> BulkPreview(BulkForm form)
    {
        var ids = await ResolveIdsAsync(form);
        if (ids is null)
        {
            return BackToList(form);
        }

        var preview = await _tools.PreviewBulkAsync(ids, form.ToChange(), CurrentCulture.Code);
        if (preview.ErrorKey is not null)
        {
            Failure(_l[preview.ErrorKey].Value);
            return BackToList(form);
        }

        return View("BulkPreview", new BulkPreviewViewModel(form, ids, preview));
    }

    [HttpPost]
    public async Task<IActionResult> BulkApply(BulkForm form)
    {
        // The exact products of the preview (the page sends their ids back).
        var result = await _tools.ApplyBulkAsync(form.Ids, form.ToChange(), AdminId);

        switch (result.Status)
        {
            case BulkApplyStatus.Applied:
                Success(_l.GetString("Admin_Bulk_Applied", result.Changed, result.Unchanged).Value);
                break;
            case BulkApplyStatus.Conflict:
                Failure(_l["Admin_Bulk_Conflict"].Value);
                break;
            case BulkApplyStatus.TooMany:
                Failure(_l.GetString("Admin_Bulk_TooMany", BulkRules.MaxProducts).Value);
                break;
            case BulkApplyStatus.NothingSelected:
                Failure(_l["Admin_Bulk_NoneSelected"].Value);
                break;
            default:
                Failure(_l["Admin_Bulk_HasProblems"].Value);
                break;
        }

        return BackToList(form);
    }

    // The ids to change, or null (with a message set) when there is nothing to do.
    private async Task<IReadOnlyList<int>?> ResolveIdsAsync(BulkForm form)
    {
        List<int> ids;

        if (form.Scope == "filter")
        {
            ids = (await _tools.FilterIdsAsync(form.ToFilter(), BulkRules.MaxProducts)).ToList();
        }
        else
        {
            ids = form.Ids.Distinct().ToList();
        }

        if (ids.Count == 0)
        {
            Failure(_l["Admin_Bulk_NoneSelected"].Value);
            return null;
        }

        if (ids.Count > BulkRules.MaxProducts)
        {
            Failure(_l.GetString("Admin_Bulk_TooMany", BulkRules.MaxProducts).Value);
            return null;
        }

        return ids;
    }

    private IActionResult BackToList(BulkForm form) =>
        !string.IsNullOrEmpty(form.Back) && Url.IsLocalUrl(form.Back) ? LocalRedirect(form.Back) : RedirectToAction(nameof(Index));

    // ---- Export and import ----

    // POST: it writes an audit line, so a link or a prefetch must not trigger it.
    [HttpPost]
    public async Task<IActionResult> ExportCsv()
    {
        var file = await _tools.ExportCsvAsync(AdminId);

        return File(file, "text/csv; charset=utf-8", $"products-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    [HttpGet]
    public IActionResult ImportTemplate()
    {
        var file = CsvWriter.Build(
            ProductCsv.Columns,
            [
                new object?[] { "NEW-001", "ToyLand", 1, 12500, 0, 10, "yes", "no", "Խաղալիք", "Игрушка", "Toy", "", "", "", "1;2" },
                new object?[] { "NEW-002", "ToyLand", 1, 8900, 15, 25, "yes", "no", "Մեքենա", "Машинка", "Car", "Նկարագրություն", "Описание", "Description", "3" }
            ]);

        return File(file, "text/csv; charset=utf-8", "products-template.csv");
    }

    [HttpGet]
    public async Task<IActionResult> Import()
    {
        return View(new ImportPageViewModel(
            await _categories.OptionsAsync(CurrentCulture.Code),
            await _ageGroups.OptionsAsync(CurrentCulture.Code)));
    }

    [HttpPost]
    [RequestSizeLimit(ProductCsv.MaxFileBytes + 100_000)]
    public async Task<IActionResult> ImportPreview(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            Failure(_l["Admin_Imp_P_NoFile"].Value);
            return RedirectToAction(nameof(Import));
        }

        if (file.Length > ProductCsv.MaxFileBytes)
        {
            Failure(_l["Admin_Imp_P_TooBig"].Value);
            return RedirectToAction(nameof(Import));
        }

        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            text = await reader.ReadToEndAsync();
        }

        var preview = await _tools.PreviewImportAsync(text);

        return View("ImportPreview", new ImportPreviewViewModel(preview, text, Path.GetFileName(file.FileName)));
    }

    [HttpPost]
    [RequestSizeLimit(ProductCsv.MaxFileBytes * 3)]
    [RequestFormLimits(ValueLengthLimit = ProductCsv.MaxFileBytes * 3)]
    public async Task<IActionResult> ImportApply(string? csv)
    {
        var result = await _tools.ApplyImportAsync(csv ?? string.Empty, AdminId);

        switch (result.Status)
        {
            case ImportApplyStatus.Applied:
                Success(_l.GetString("Admin_Imp_Done", result.Created, result.Updated, result.Unchanged).Value);
                return RedirectToAction(nameof(Index));
            case ImportApplyStatus.Conflict:
                Failure(_l["Admin_Imp_Conflict"].Value);
                break;
            default:
                Failure(_l["Admin_Imp_Invalid"].Value);
                break;
        }

        return RedirectToAction(nameof(Import));
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
