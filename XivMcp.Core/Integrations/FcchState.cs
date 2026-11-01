namespace XivMcp.Integrations;

/// <summary>Whether FCCH (FC chest automation) really is busy.</summary>
public static class FcchState
{
    /// <summary>
    /// FCCH reports busy for as long as the character visits another world, where it can't reach a company chest anyway: that
    /// doesn't count. An unknown world (0, e.g. while loading) trusts what FCCH reports.
    /// </summary>
    public static bool IsBusy(bool reportsBusy, uint currentWorld, uint homeWorld) =>
        reportsBusy && (currentWorld == 0 || homeWorld == 0 || currentWorld == homeWorld);
}
