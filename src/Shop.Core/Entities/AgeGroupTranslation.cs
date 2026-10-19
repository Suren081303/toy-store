using Shop.Core.Localization;

namespace Shop.Core.Entities;

public class AgeGroupTranslation : ITranslation
{
    public int AgeGroupId { get; set; }

    public string Culture { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AgeGroup AgeGroup { get; set; } = null!;
}
