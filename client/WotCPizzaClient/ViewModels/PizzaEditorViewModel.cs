using System.Collections.ObjectModel;
using System.Globalization;
using WotCPizzaClient.Models;
using WotCPizzaClient.Resources;
using WotCPizzaClient.Services;

namespace WotCPizzaClient.ViewModels;

public sealed record SizeChoice(PizzaSize Size)
{
  public string Label => $"{Size.Name} — {Pricing.Dollars(Size.CostCents)}";
}

public sealed class ToppingChoice(ToppingItem topping, Action changed) : ObservableObject
{
  private bool selected;
  public uint Code => topping.Code;
  public string Name => topping.Name;
  public bool Selected { get => selected; set { if (Set(ref selected, value)) changed(); } }
}

public sealed class CategoryChoice(ToppingCategory category, Action changed) : ObservableObject
{
  private long baseCents;
  public uint Code => category.Code;
  public ObservableCollection<ToppingChoice> Toppings { get; } = new(category.Toppings.Select(t => new ToppingChoice(t, changed)));
  public string Label => $"{category.Name} — {Pricing.Dollars(baseCents * category.CostBasisPoints / Pricing.RateScale)}";
  internal void SetBase(long cents) { baseCents = cents; OnPropertyChanged(nameof(Label)); }
}

public sealed class PizzaEditorViewModel : ObservableObject
{
  private readonly Menu menu;
  private SizeChoice selectedSize;
  private CategoryChoice? selectedCategory;
  private string quantity = "1";
  private string error = "";
  private long unit;
  private long total;

  /// <summary>Creates a detached editing session, preserving selections across category filters.</summary>
  public PizzaEditorViewModel(Menu menu, PizzaOrder? pizza = null)
  {
    this.menu = menu;
    Sizes = new(menu.Pizzas.Select(p => new SizeChoice(p)));
    selectedSize = Sizes.FirstOrDefault(p => p.Size.Code == pizza?.PizzaCode) ?? Sizes[0];
    Categories = new(menu.Toppings.Select(t => new CategoryChoice(t, Recalculate)));
    selectedCategory = Categories.FirstOrDefault();
    if (pizza is not null)
    {
      quantity = pizza.Count.ToString(CultureInfo.InvariantCulture);
      foreach (var category in Categories)
        foreach (var topping in category.Toppings)
          topping.Selected = pizza.Toppings.Any(t => t.ToppingBaseCode == category.Code && t.Toppings.Contains(topping.Code));
    }
    ButtonLabel = Text.Get(pizza is null ? "AddPizza" : "UpdatePizza");
    Recalculate();
  }

  public ObservableCollection<SizeChoice> Sizes { get; }
  public ObservableCollection<CategoryChoice> Categories { get; }
  public SizeChoice SelectedSize { get => selectedSize; set { if (value is not null && Set(ref selectedSize, value)) Recalculate(); } }
  public CategoryChoice? SelectedCategory { get => selectedCategory; set => Set(ref selectedCategory, value); }
  public string Quantity { get => quantity; set { if (Set(ref quantity, value)) Recalculate(); } }
  public string Error { get => error; private set => Set(ref error, value); }
  public string UnitPrice => Pricing.Dollars(unit);
  public string Total => Pricing.Dollars(total);
  public string ButtonLabel { get; }
  public bool CanSave => Error.Length == 0;

  /// <summary>Returns a fresh validated pizza, without changing the source order.</summary>
  public PizzaOrder Build()
  {
    if (!uint.TryParse(Quantity, NumberStyles.None, CultureInfo.InvariantCulture, out uint count) || count is < 1 or > 100)
      throw new OrderValidationException(Text.Get("QuantityInvalid"));
    return new(SelectedSize.Size.Code, count, Categories.Where(c => c.Toppings.Any(t => t.Selected))
      .Select(c => new ToppingSelection(c.Code, c.Toppings.Where(t => t.Selected).Select(t => t.Code).ToArray())).ToArray());
  }

  private void Recalculate()
  {
    try
    {
      foreach (var category in Categories) category.SetBase(SelectedSize.Size.CostCents);
      var pizza = Build();
      unit = Pricing.UnitPrice(menu, pizza);
      total = unit * pizza.Count;
      if (total > Pricing.MaxMoney) throw new OrderValidationException(Text.Get("AmountLimit"));
      Error = "";
    }
    catch (OrderValidationException exception) { Error = exception.Message; unit = total = 0; }
    OnPropertyChanged(nameof(UnitPrice));
    OnPropertyChanged(nameof(Total));
    OnPropertyChanged(nameof(CanSave));
  }
}
