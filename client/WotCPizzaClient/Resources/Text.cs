using System.Globalization;
using System.Resources;

namespace WotCPizzaClient.Resources;

public sealed class Text
{
  private static readonly ResourceManager Manager = new("WotCPizzaClient.Resources.Strings", typeof(Text).Assembly);
  public static Text Instance { get; } = new();
  public string this[string key] => Get(key);
  /// <summary>Looks up a localized message and formats its arguments.</summary>
  public static string Get(string key, params object[] args) =>
    string.Format(CultureInfo.CurrentCulture, Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key, args);
}
