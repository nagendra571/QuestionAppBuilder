namespace QuestionAppBuilder.SampleHost.DevAuth;

public sealed class DevUserStore
{
    public const string SharedPassword = "DevPassword!1";

    private static readonly IReadOnlyDictionary<string, string> Users =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["designer"] = "Designer",
            ["filler"] = "Filler",
            ["filler2"] = "Filler",
            ["analyst"] = "Analyst",
        };

    public bool TryValidate(string username, string password, out string role)
    {
        role = string.Empty;

        if (!Users.TryGetValue(username, out var userRole) ||
            !string.Equals(password, SharedPassword, StringComparison.Ordinal))
        {
            return false;
        }

        role = userRole;
        return true;
    }
}
