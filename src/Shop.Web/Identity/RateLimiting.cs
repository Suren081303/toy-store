namespace Shop.Web.Identity;

public static class RateLimitPolicies
{
    // Applied to the account forms (sign in, register, forgot password, resend).
    public const string Account = "account";
}

public class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    // Requests per minute from one IP address to the account forms.
    public int AccountPermitLimit { get; set; } = 10;
}
