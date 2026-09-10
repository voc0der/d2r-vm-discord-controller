namespace D2RHost;

/// <summary>What the host decided about one incoming API request's credentials.</summary>
public enum HostApiAuthorization
{
    /// <summary>The key checks out; run the command.</summary>
    Allowed,

    /// <summary>Nobody has turned the API on, so there is nothing to authenticate against.</summary>
    Disabled,

    /// <summary>The API is on and the presented key is missing or wrong.</summary>
    Unauthorized
}

/// <summary>
/// The gate on every <c>/api</c> request, kept free of ASP.NET types so the decision itself can be
/// asserted directly rather than through a live web host.
/// </summary>
internal static class HostApiAuthorizationPolicy
{
    /// <summary>
    /// "Turned off" and "wrong key" are deliberately distinct outcomes. An operator debugging their
    /// own automation needs to tell "I never ran /d2r config api" from "my key is wrong", and on a
    /// surface already limited to the trusted network, blurring the two only costs them time.
    /// </summary>
    public static HostApiAuthorization Evaluate(
        bool enabled,
        string? storedKeyHash,
        string? authorizationHeader,
        string? apiKeyHeader)
    {
        // A key hash is as necessary as the flag: an enabled API with nothing to check against
        // must refuse everything rather than accept anything.
        if (!enabled || string.IsNullOrWhiteSpace(storedKeyHash))
        {
            return HostApiAuthorization.Disabled;
        }

        var presented = HostApiKey.ReadPresentedKey(authorizationHeader, apiKeyHeader);
        return HostApiKey.Matches(presented, storedKeyHash)
            ? HostApiAuthorization.Allowed
            : HostApiAuthorization.Unauthorized;
    }
}
