namespace EAuction.AuctionAdmin.Domain;

/// <summary>
/// One platform setting an administrator changes, stored as JSON under its key.
/// A handful of rows, each read and written whole — today only
/// «إعدادات العرض للزوار» (<see cref="PublicVisibilityChanged"/>).
/// </summary>
public sealed class PlatformSetting
{
    public required string Key { get; init; }
    public string ValueJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedByUserId { get; set; }
}
