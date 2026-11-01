using Microsoft.VisualStudio.TestTools.UnitTesting;
using WotCPizzaClient.Models;
using WotCPizzaClient.ViewModels;

namespace WotCPizzaClient.Tests;

[TestClass]
public class PizzaEditorViewModelTests
{
  /// <summary>A popup edits its own copy until confirmation.</summary>
  [TestMethod]
  public void WhenEditorChangesToppingThenSourcePizzaIsUnchanged()
  {
    var source = new PizzaOrder(0, 1, [new(0, [0])]);
    var editor = new PizzaEditorViewModel(TestData.Menu(), source);
    editor.Categories[0].Toppings[1].Selected = true;
    CollectionAssert.AreEqual(new uint[] { 0 }, source.Toppings[0].Toppings);
  }

  /// <summary>Changing the category filter must not lose checked toppings.</summary>
  [TestMethod]
  public void WhenCategoryChangesThenSelectedToppingsArePreserved()
  {
    var editor = new PizzaEditorViewModel(TestData.Menu());
    editor.Categories[0].Toppings[0].Selected = true;
    editor.SelectedCategory = editor.Categories[1];
    editor.Categories[1].Toppings[0].Selected = true;
    Assert.AreEqual(2, editor.Build().Toppings.Length);
  }

  /// <summary>Topping prices follow the newly chosen base size.</summary>
  [TestMethod]
  public void WhenSizeChangesThenUnitPriceRecalculates()
  {
    var editor = new PizzaEditorViewModel(TestData.Menu());
    editor.Categories[0].Toppings[0].Selected = true;
    editor.SelectedSize = editor.Sizes[2];
    Assert.AreEqual("$12.96", editor.UnitPrice);
  }

  /// <summary>Invalid quantities disable the confirmation button.</summary>
  [DataTestMethod]
  [DataRow("0")]
  [DataRow("101")]
  [DataRow("1.5")]
  public void WhenQuantityIsInvalidThenEditorCannotSave(string quantity)
  {
    var editor = new PizzaEditorViewModel(TestData.Menu()) { Quantity = quantity };
    Assert.IsFalse(editor.CanSave);
  }
}
