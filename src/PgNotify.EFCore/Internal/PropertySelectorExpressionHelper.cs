using System.Linq.Expressions;

namespace PgNotify.Internal;

/// <summary>
/// Parses the same <c>x =&gt; x.Property</c> / <c>x =&gt; new { x.A, x.B }</c> expression shapes
/// EF Core's own <c>HasKey</c>/<c>HasIndex</c> accept, used by <c>OnUpdate(x =&gt; ...)</c> to
/// select watched properties.
/// </summary>
public static class PropertySelectorExpressionHelper
{
    /// <summary>Extracts the selected property names, in the order they appear in the expression.</summary>
    /// <exception cref="ArgumentException">The expression is not a single property or an anonymous-type property list.</exception>
    public static IReadOnlyList<string> GetPropertyNames<TEntity>(Expression<Func<TEntity, object?>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var body = expression.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        return body switch
        {
            NewExpression { Members: not null } newExpression => newExpression.Members.Select(m => m.Name).ToArray(),
            MemberExpression member => [member.Member.Name],
            _ => throw new ArgumentException(
                                $"Expression '{expression}' is not a valid property selector. Use a single property "
                                + "(x => x.Name) or an anonymous type of properties (x => new { x.Name, x.Email }).",
                                nameof(expression)),
        };
    }
}
