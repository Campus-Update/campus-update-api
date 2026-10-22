using CampusUpdate.Domain.Common;

namespace CampusUpdate.Domain.Schools;

public sealed class PlatformSettings : Entity
{
    public const string SingletonKey = "platform";

    public string Key { get; set; } = SingletonKey;
    public string PlatformName { get; set; } = "Campus Update";
    public string? SupportEmail { get; set; }
    public string? SupportPhone { get; set; }
    public bool MaintenanceMode { get; set; }
    public decimal ListingFee { get; set; }
    public int InvoiceDays { get; set; } = 7;
    public bool HoldEventsUntilPayment { get; set; }
}
