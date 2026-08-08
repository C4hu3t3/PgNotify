using PgNotify.Internal;

namespace PgNotify.EFCore.Tests;

public class PropertySelectorExpressionHelperTests
{
    private sealed class Widget
    {
        public int Age { get; set; }

        public string Name { get; set; } = "";

        public string Email { get; set; } = "";
    }

    [Fact]
    public void A_single_reference_type_property_selector_returns_its_name()
    {
        var names = PropertySelectorExpressionHelper.GetPropertyNames<Widget>(w => w.Name);

        names.Should().Equal("Name");
    }

    [Fact]
    public void A_single_value_type_property_selector_unwraps_the_boxing_convert_and_returns_its_name()
    {
        // int (a value type) reaches the body wrapped in a Convert node the compiler inserts to
        // satisfy Expression<Func<TEntity, object?>> - a reference-type property like Name above
        // compiles straight to a MemberExpression with no wrapping node at all, so this exercises
        // the unwrap branch that string-typed selectors never touch.
        var names = PropertySelectorExpressionHelper.GetPropertyNames<Widget>(w => w.Age);

        names.Should().Equal("Age");
    }

    [Fact]
    public void An_anonymous_type_selector_returns_every_member_in_declaration_order()
    {
        var names = PropertySelectorExpressionHelper.GetPropertyNames<Widget>(w => new { w.Name, w.Email });

        names.Should().Equal("Name", "Email");
    }

    [Fact]
    public void A_non_property_expression_throws()
    {
        var act = () => PropertySelectorExpressionHelper.GetPropertyNames<Widget>(w => w.ToString());

        act.Should().Throw<ArgumentException>().WithMessage("*not a valid property selector*");
    }

    [Fact]
    public void A_null_expression_throws()
    {
        var act = () => PropertySelectorExpressionHelper.GetPropertyNames<Widget>(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
