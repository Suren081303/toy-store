namespace Shop.Core.Admin;

// ---- Photos ----

public static class ImageRules
{
    public const int MaxPerProduct = 6;

    // The uploaded file, before it is processed.
    public const long MaxBytes = 5 * 1024 * 1024;

    // The stored photo and its small version are scaled down to fit these sizes (never enlarged).
    public const int MaxFullSize = 1000;

    public const int MaxThumbSize = 400;

    // A photo wider or taller than this is refused (a tiny file can hold a huge picture that would eat the memory).
    public const int MaxSourceDimension = 12000;

    // ... and no more than this many pixels in all (40 million = for example 8000 x 5000).
    public const long MaxSourcePixels = 40_000_000;
}

// Where a processed photo was saved, as web paths such as /uploads/products/12/ab12.jpg
public record StoredImage(string Path, string? ThumbPath);

// Image is set when the photo was accepted; otherwise ErrorKey is the translation key of the reason.
public record ImageStoreResult(StoredImage? Image, string? ErrorKey);

public interface IImageStore
{
    // Checks that the stream is really a JPEG, PNG or WebP picture of a sensible size, then saves a cleaned, scaled-down JPEG
    // (and a small one when makeThumb is true). Everything stored about the camera or the place is dropped.
    Task<ImageStoreResult> SaveAsync(Stream content, long length, string folder, bool makeThumb);

    // Deletes the files of an uploaded photo. Paths outside the uploads folder (the placeholder pictures) are never touched.
    void Delete(params string?[] paths);

    // A copy of an uploaded photo (and its small version) under new random names in another folder, so the copy stays when the
    // original is deleted. A placeholder picture (not in the uploads folder) is shared, not copied: the same paths come back.
    // Null when the photo's file is missing.
    Task<StoredImage?> CopyAsync(StoredImage source, string folder);
}

// ---- Categories and age groups ----

public enum DeleteStatus
{
    Deleted,

    NotFound,

    // Products still use it: hide it instead.
    InUse
}

public record AdminCategoryRow(int Id, string Name, int ProductCount, int SortOrder, bool IsActive, string? ImagePath);

public class AdminCategoryForm
{
    public int? Id { get; set; }

    public string NameHy { get; set; } = string.Empty;

    public string NameRu { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public string? ImagePath { get; set; }
}

public record DeleteCategoryResult(DeleteStatus Status, string? ImagePath = null);

// Id is null when the category to change does not exist. ReplacedImagePath is the old picture's path when a new one took its place
// (the caller deletes that file).
public record SaveCategoryResult(int? Id, string? ReplacedImagePath = null);

public interface IAdminCategoryService
{
    Task<IReadOnlyList<AdminCategoryRow>> ListAsync(string culture);

    Task<AdminCategoryForm?> GetAsync(int id);

    // Creates (Id = null) or changes a category.
    Task<SaveCategoryResult> SaveAsync(AdminCategoryForm form, string adminId);

    Task<DeleteCategoryResult> DeleteAsync(int id, string adminId);

    // For the product form: every category, hidden ones too.
    Task<IReadOnlyList<AdminOption>> OptionsAsync(string culture);
}

public record AdminOption(int Id, string Name);

public record AdminAgeGroupRow(int Id, string Name, int ProductCount, int SortOrder, bool IsActive);

public class AdminAgeGroupForm
{
    public int? Id { get; set; }

    public string NameHy { get; set; } = string.Empty;

    public string NameRu { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}

public interface IAdminAgeGroupService
{
    Task<IReadOnlyList<AdminAgeGroupRow>> ListAsync(string culture);

    Task<AdminAgeGroupForm?> GetAsync(int id);

    Task<int?> SaveAsync(AdminAgeGroupForm form, string adminId);

    Task<DeleteStatus> DeleteAsync(int id, string adminId);

    Task<IReadOnlyList<AdminOption>> OptionsAsync(string culture);
}

// ---- Products ----

public enum ProductVisibility
{
    // Products customers can buy (not archived). The default.
    Active,

    Archived,

    All
}

public class AdminProductFilter
{
    // Part of the SKU, brand or any language's name.
    public string? Query { get; set; }

    public int? CategoryId { get; set; }

    public ProductVisibility Show { get; set; } = ProductVisibility.Active;

    // Only products with StockLevels.Low or less in stock.
    public bool LowStock { get; set; }

    public int Page { get; set; } = 1;
}

public record AdminProductRow(
    int Id,
    string Sku,
    string Name,
    string Brand,
    string CategoryName,
    decimal Price,
    int DiscountPercent,
    int StockQuantity,
    bool IsArchived,
    string? ImagePath);

public class AdminProductForm
{
    public int? Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    public string Brand { get; set; } = string.Empty;

    public int CategoryId { get; set; }

    public decimal Price { get; set; }

    public int DiscountPercent { get; set; }

    public int StockQuantity { get; set; }

    public string NameHy { get; set; } = string.Empty;

    public string NameRu { get; set; } = string.Empty;

    public string NameEn { get; set; } = string.Empty;

    public string DescriptionHy { get; set; } = string.Empty;

    public string DescriptionRu { get; set; } = string.Empty;

    public string DescriptionEn { get; set; } = string.Empty;

    public List<int> AgeGroupIds { get; set; } = new();

    // Forces the product into "New arrivals" even when it is old.
    public bool IsPinnedAsNew { get; set; }

    // Hidden from the shop but kept (old orders refer to it).
    public bool IsArchived { get; set; }

    // The version of the product when the form was opened (base64). If the product changed since then, saving is refused.
    public string? RowVersion { get; set; }
}

public record AdminProductImage(int Id, string Path, string? ThumbPath, int SortOrder);

public record AdminProduct(AdminProductForm Form, IReadOnlyList<AdminProductImage> Images);

public enum SaveProductStatus
{
    Saved,

    NotFound,

    DuplicateSku,

    UnknownCategory,

    // Somebody (or an order) changed the product after the form was opened.
    Conflict
}

public record SaveProductResult(SaveProductStatus Status, int? Id = null);

public enum ImageChangeStatus
{
    Done,

    NotFound,

    // More than ImageRules.MaxPerProduct photos.
    TooMany
}

public record RemovedImage(string Path, string? ThumbPath);

public interface IAdminProductService
{
    Task<PagedResult<AdminProductRow>> ListAsync(AdminProductFilter filter, string culture);

    Task<AdminProduct?> GetAsync(int id);

    Task<SaveProductResult> SaveAsync(AdminProductForm form, string adminId);

    // The quick "stock" box of the list: sets the number directly. False when the product does not exist.
    Task<bool> SetStockAsync(int productId, int stock, string adminId);

    Task<int> CountImagesAsync(int productId);

    Task<ImageChangeStatus> AddImagesAsync(int productId, IReadOnlyList<StoredImage> images, string adminId);

    // Returns the removed photo's paths, so the caller can delete the files; null when it does not exist.
    Task<RemovedImage?> RemoveImageAsync(int productId, int imageId, string adminId);

    // direction -1 = earlier, +1 = later.
    Task<ImageChangeStatus> MoveImageAsync(int productId, int imageId, int direction, string adminId);

    Task<ImageChangeStatus> MakeMainImageAsync(int productId, int imageId, string adminId);
}
