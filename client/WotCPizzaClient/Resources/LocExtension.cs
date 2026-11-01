using System.Windows.Markup;

namespace WotCPizzaClient.Resources;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
  /// <summary>Resolves localized text when a view is created.</summary>
  public override object ProvideValue(IServiceProvider serviceProvider) => Text.Get(key);
}
