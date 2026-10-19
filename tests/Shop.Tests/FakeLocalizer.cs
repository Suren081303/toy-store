using Microsoft.Extensions.Localization;
using Shop.Web;

namespace Shop.Tests;

// A localizer that returns the key itself, so tests can see which message was chosen.
public class FakeLocalizer : IStringLocalizer<SharedResource>
{
    public LocalizedString this[string name] => new(name, name);

    public LocalizedString this[string name, params object[] arguments] => new(name, name);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
}
