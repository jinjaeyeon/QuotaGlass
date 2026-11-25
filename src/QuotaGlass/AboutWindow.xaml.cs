using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace QuotaGlass;

public partial class AboutWindow : Window
{
    private const string RepositoryAddress =
        "https://github.com/jinjaeyeon/QuotaGlass";

    public AboutWindow(Version version)
    {
        InitializeComponent();
        VersionText.Text = $"v{version.ToString(3)}";
        BuildDateText.Text = GetBuildDate();
        RepositoryLink.NavigateUri = new Uri(RepositoryAddress);
    }

    private static string GetBuildDate()
    {
        var value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(
                    attribute.Key,
                    "BuildDate",
                    StringComparison.Ordinal))?.Value;
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var buildDate))
        {
            return "알 수 없음";
        }

        return buildDate.ToString(
            "yyyy년 M월 d일",
            CultureInfo.GetCultureInfo("ko-KR"));
    }

    private void RepositoryLink_RequestNavigate(
        object sender,
        RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        e.Handled = true;
    }
}