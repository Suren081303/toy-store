using Microsoft.EntityFrameworkCore;
using Shop.Core.Entities;

namespace Shop.Infrastructure.Data;

// Development placeholder data. Runs only when the database has no categories.
public static class DbSeeder
{
    private record CategorySeed(
        string Key, string Hy, string Ru, string En,
        int BaseAge, int BasePrice,
        string ProductHy, string ProductRu, string ProductEn);

    private record AgeGroupSeed(string Hy, string Ru, string En);

    private static readonly CategorySeed[] CategorySeeds =
    [
        new("edu", "Ուսուցողական", "Развивающие", "Educational", 1, 12900,
            "Փայտե ուսուցողական խաղ", "Деревянная развивающая игра", "Wooden learning toy"),
        new("soft", "Փափուկ խաղալիքներ", "Мягкие игрушки", "Soft toys", 0, 8500,
            "Փափուկ նապաստակ", "Мягкий зайчик", "Plush bunny"),
        new("cars", "Մեքենաներ և տիկնիկներ", "Машинки и куклы", "Cars & dolls", 2, 24900,
            "Հեռակառավարվող մեքենա", "Радиоуправляемая машинка", "Remote-control car"),
        new("blocks", "Կառուցողական", "Конструкторы", "Building toys", 1, 19900,
            "Գունավոր կառուցողական հավաքածու", "Цветной конструктор", "Color building set"),
        new("games", "Սեղանի խաղեր", "Настольные игры", "Board games", 2, 17500,
            "Ընտանեկան սեղանի խաղ", "Настольная игра для семьи", "Family board game"),
        new("baby", "Մանկական", "Для малышей", "Baby toys", 0, 6900,
            "Զարգացնող մանկական խաղալիք", "Развивающая игрушка для малыша", "Baby activity toy"),
        new("outdoor", "Բացօթյա խաղեր", "Игры на улице", "Outdoor toys", 2, 32900,
            "Մանկական ինքնագլոր", "Детский самокат", "Kids' scooter"),
        new("collect", "Հավաքածուներ", "Коллекционные", "Collectibles", 4, 28900,
            "Հավաքածուի ֆիգուր", "Коллекционная фигурка", "Collectible figure")
    ];

    // The last one (index 4) is "Collectors".
    private static readonly AgeGroupSeed[] AgeGroupSeeds =
    [
        new("0–2 տ.", "0–2 лет", "0–2 yrs"),
        new("3–5 տ.", "3–5 лет", "3–5 yrs"),
        new("6–9 տ.", "6–9 лет", "6–9 yrs"),
        new("10–14 տ.", "10–14 лет", "10–14 yrs"),
        new("Հավաքորդներ", "Коллекционеры", "Collectors")
    ];

    private static readonly string[] Brands = ["PlayCo", "Kidoz", "ToyLand", "BrickMaster"];

    public static async Task SeedAsync(ShopDbContext db, TimeProvider time)
    {
        if (await db.Categories.AnyAsync())
        {
            return;
        }

        var now = time.GetUtcNow().UtcDateTime;

        var ageGroups = AgeGroupSeeds
            .Select((seed, index) => new AgeGroup
            {
                SortOrder = index + 1,
                Translations =
                {
                    new AgeGroupTranslation { Culture = "hy", Name = seed.Hy },
                    new AgeGroupTranslation { Culture = "ru", Name = seed.Ru },
                    new AgeGroupTranslation { Culture = "en", Name = seed.En }
                }
            })
            .ToList();

        var categories = CategorySeeds
            .Select((seed, index) => new Category
            {
                SortOrder = index + 1,
                ImagePath = $"/images/placeholders/cat-{seed.Key}.svg",
                Translations =
                {
                    new CategoryTranslation { Culture = "hy", Name = seed.Hy },
                    new CategoryTranslation { Culture = "ru", Name = seed.Ru },
                    new CategoryTranslation { Culture = "en", Name = seed.En }
                }
            })
            .ToList();

        var products = new List<Product>();
        for (var c = 0; c < CategorySeeds.Length; c++)
        {
            for (var j = 0; j < 3; j++)
            {
                products.Add(CreateProduct(CategorySeeds[c], categories[c], ageGroups, c * 3 + j, j, now));
            }
        }

        db.AgeGroups.AddRange(ageGroups);
        db.Categories.AddRange(categories);
        db.Products.AddRange(products);
        await db.SaveChangesAsync();
    }

    private static Product CreateProduct(
        CategorySeed seed, Category category, List<AgeGroup> ageGroups, int i, int j, DateTime now)
    {
        var product = new Product
        {
            Sku = $"TOY-{seed.Key.ToUpperInvariant()}-{j + 1:00}",
            Brand = Brands[i % Brands.Length],
            Category = category,
            Price = seed.BasePrice + j * 3500,
            DiscountPercent = i % 4 == 1 ? 10 : i % 6 == 2 ? 20 : i % 5 == 4 ? 15 : 0,
            StockQuantity = i % 7 == 3 ? 0 : i % 5 == 0 ? 2 : 5 + (i * 3) % 20,
            // Products 0,1,2 of every 8 are recent; the rest are older than the 30-day new-arrival period.
            CreatedAt = now.AddDays(-(i % 8 < 3 ? (i % 8) * 6 + 2 : 40 + i * 3)),
            IsPinnedAsNew = i == 12 || i == 20,
            IsArchived = i == 22 || i == 23
        };

        var nameHy = j == 0 ? seed.ProductHy : $"{seed.Hy} #{j + 1}";
        var nameRu = j == 0 ? seed.ProductRu : $"{seed.Ru} #{j + 1}";
        var nameEn = j == 0 ? seed.ProductEn : $"{seed.En} #{j + 1}";

        product.Translations.Add(new ProductTranslation
        {
            Culture = "hy",
            Name = nameHy,
            Description = "Նկարագրության տեղապահ տեքստ։ Այն կփոխարինվի իրական տեքստով։"
        });

        // Some products have no ru or en text, so the Armenian fallback can be seen.
        if (i % 5 != 4)
        {
            product.Translations.Add(new ProductTranslation
            {
                Culture = "ru",
                Name = nameRu,
                Description = "Текст-заполнитель описания. Будет заменён настоящим текстом."
            });
        }

        if (i % 7 != 6)
        {
            product.Translations.Add(new ProductTranslation
            {
                Culture = "en",
                Name = nameEn,
                Description = "Placeholder description. It will be replaced with the real text."
            });
        }

        var ageIndexes = new HashSet<int>();
        if (seed.BaseAge == 4)
        {
            ageIndexes.Add(4);
        }
        else
        {
            ageIndexes.Add(Math.Min(seed.BaseAge + (j == 1 ? 1 : 0), 3));
            if (j == 2)
            {
                ageIndexes.Add(Math.Min(seed.BaseAge + 1, 3));
            }
        }

        foreach (var ageIndex in ageIndexes)
        {
            product.AgeGroups.Add(ageGroups[ageIndex]);
        }

        product.Images.Add(new ProductImage { Path = $"/images/placeholders/cat-{seed.Key}.svg", SortOrder = 1 });
        if (j != 2)
        {
            product.Images.Add(new ProductImage { Path = "/images/placeholders/generic-a.svg", SortOrder = 2 });
            product.Images.Add(new ProductImage { Path = "/images/placeholders/generic-b.svg", SortOrder = 3 });
        }

        return product;
    }
}
