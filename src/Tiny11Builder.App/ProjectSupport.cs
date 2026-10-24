namespace Tiny11Builder.App;

internal enum SupportDestination { Sponsors, Kofi, Patreon, PayPal }

internal static class ProjectSupport
{
    public const string Purpose = "用于支持开发时间、各 Windows 版本的测试以及针对反馈问题的修复。应用的全部功能保持免费。";

    public static Uri Link(SupportDestination destination) => new(destination switch
    {
        SupportDestination.Sponsors => "https://github.com/sponsors/ntdevlabs",
        SupportDestination.Kofi => "https://ko-fi.com/ntdev",
        SupportDestination.Patreon => "https://www.patreon.com/ntdev",
        SupportDestination.PayPal => "https://paypal.me/ntdev2",
        _ => throw new ArgumentOutOfRangeException(nameof(destination))
    });
}
