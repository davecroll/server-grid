using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace ServerGrid.Grid;

/// <summary>Controls how string filters are emitted.</summary>
public enum StringMatchMode
{
    /// <summary>Use <c>string.Contains(value, StringComparison.OrdinalIgnoreCase)</c> etc. Fast for LINQ-to-Objects.</summary>
    OrdinalIgnoreCase,
    /// <summary>Use plain <c>string.Contains(value)</c> so database providers (EF Core) can translate it; case handling is left to the DB collation.</summary>
    Provider,
}

/// <summary>
/// Turns <see cref="GridRequest{TItem}"/> state into expression trees. Because the output is an expression
/// (not a delegate) the same builder serves in-memory lists and IQueryable providers such as EF Core.
/// </summary>
public static class GridExpressionBuilder
{
    private static readonly MethodInfo StringContainsCmp = typeof(string).GetMethod(nameof(string.Contains), [typeof(string), typeof(StringComparison)])!;
    private static readonly MethodInfo StringStartsWithCmp = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string), typeof(StringComparison)])!;
    private static readonly MethodInfo StringEndsWithCmp = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string), typeof(StringComparison)])!;
    private static readonly MethodInfo StringEqualsCmp = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo StringIsNullOrEmpty = typeof(string).GetMethod(nameof(string.IsNullOrEmpty), [typeof(string)])!;
    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    /// <summary>
    /// Builds the row predicate for the request, or <c>null</c> when nothing filters.
    /// <paramref name="ignoreColumnKey"/> excludes one column's filter (used for Excel-style value lists).
    /// </summary>
    public static Expression<Func<TItem, bool>>? BuildPredicate<TItem>(GridRequest<TItem> request, string? ignoreColumnKey = null, StringMatchMode mode = StringMatchMode.OrdinalIgnoreCase)
    {
        var row = Expression.Parameter(typeof(TItem), "row");
        Expression? body = null;

        foreach (var filter in request.Filters)
        {
            if (!filter.IsActive || filter.ColumnKey == ignoreColumnKey) continue;
            var column = request.GetColumn(filter.ColumnKey);
            var member = Rebind(column.Selector, row);

            if (filter.HasCondition && BuildCondition(member, column, filter, mode) is { } cond)
                body = And(body, cond);
            if (filter.HasValueSet)
                body = And(body, BuildValueSet(member, column, filter));
        }

        if (!string.IsNullOrWhiteSpace(request.QuickSearch))
        {
            Expression? any = null;
            var term = Expression.Constant(request.QuickSearch.Trim(), typeof(string));
            foreach (var column in request.Columns.Where(c => c.QuickSearchable && c.ValueType == typeof(string)))
            {
                var member = Rebind(column.Selector, row);
                var test = Expression.AndAlso(Expression.NotEqual(member, Expression.Constant(null, typeof(string))), StringCall(member, term, "Contains", mode));
                any = any is null ? test : Expression.OrElse(any, test);
            }
            if (any is not null) body = And(body, any);
        }

        return body is null ? null : Expression.Lambda<Func<TItem, bool>>(body, row);
    }

    /// <summary>Applies OrderBy/ThenBy calls for each sort descriptor. <paramref name="stringComparer"/> is used for string columns when supplied (in-memory only).</summary>
    public static IQueryable<TItem> ApplySorting<TItem>(IQueryable<TItem> source, GridRequest<TItem> request, IComparer<string>? stringComparer = null)
    {
        var first = true;
        foreach (var sort in request.Sorts)
        {
            var column = request.GetColumn(sort.ColumnKey);
            var method = (first, sort.Descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending),
            };
            var typeArgs = new[] { typeof(TItem), column.ValueType };
            var call = column.ValueType == typeof(string) && stringComparer is not null
                ? Expression.Call(typeof(Queryable), method, typeArgs, source.Expression, Expression.Quote(column.Selector), Expression.Constant(stringComparer, typeof(IComparer<string>)))
                : Expression.Call(typeof(Queryable), method, typeArgs, source.Expression, Expression.Quote(column.Selector));
            source = source.Provider.CreateQuery<TItem>(call);
            first = false;
        }
        return source;
    }

    /// <summary>Operators that make sense for a given kind of column.</summary>
    public static IReadOnlyList<FilterOperator> OperatorsFor(GridValueKind kind, bool nullable) => kind switch
    {
        GridValueKind.Text =>
        [
            FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.EqualTo, FilterOperator.NotEqualTo,
            FilterOperator.StartsWith, FilterOperator.EndsWith, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty,
        ],
        GridValueKind.Number or GridValueKind.Date => nullable
            ? [FilterOperator.EqualTo, FilterOperator.NotEqualTo, FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual, FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty]
            : [FilterOperator.EqualTo, FilterOperator.NotEqualTo, FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual, FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between],
        GridValueKind.Enum or GridValueKind.Boolean => nullable
            ? [FilterOperator.EqualTo, FilterOperator.NotEqualTo, FilterOperator.IsEmpty, FilterOperator.IsNotEmpty]
            : [FilterOperator.EqualTo, FilterOperator.NotEqualTo],
        _ => nullable ? [FilterOperator.IsEmpty, FilterOperator.IsNotEmpty] : [],
    };

    public static string Describe(FilterOperator op) => op switch
    {
        FilterOperator.Contains => "contains",
        FilterOperator.NotContains => "does not contain",
        FilterOperator.EqualTo => "equals",
        FilterOperator.NotEqualTo => "does not equal",
        FilterOperator.StartsWith => "begins with",
        FilterOperator.EndsWith => "ends with",
        FilterOperator.GreaterThan => "is greater than",
        FilterOperator.GreaterThanOrEqual => "is greater than or equal to",
        FilterOperator.LessThan => "is less than",
        FilterOperator.LessThanOrEqual => "is less than or equal to",
        FilterOperator.Between => "is between",
        FilterOperator.IsEmpty => "is blank",
        FilterOperator.IsNotEmpty => "is not blank",
        _ => op.ToString(),
    };

    public static bool NeedsOneOperand(FilterOperator op) => op is not (FilterOperator.IsEmpty or FilterOperator.IsNotEmpty);
    public static bool NeedsTwoOperands(FilterOperator op) => op == FilterOperator.Between;

    /// <summary>Converts a parsed UI value (string, number, boxed value) to the column's non-nullable CLR type.</summary>
    public static object? ConvertValue(object? value, Type target)
    {
        if (value is null) return null;
        target = Nullable.GetUnderlyingType(target) ?? target;
        if (target.IsInstanceOfType(value)) return value;

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (target.IsEnum) return Enum.Parse(target, text, ignoreCase: true);
        if (target == typeof(DateOnly)) return value is DateTime dt ? DateOnly.FromDateTime(dt) : DateOnly.Parse(text, CultureInfo.InvariantCulture);
        if (target == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.InvariantCulture);
        if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        if (target == typeof(TimeOnly)) return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
        if (target == typeof(Guid)) return Guid.Parse(text);
        if (target == typeof(bool)) return text is "1" or "true" or "True" or "yes" or "Yes";
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    // ---- internals -------------------------------------------------------------------------------

    private static Expression Rebind(LambdaExpression selector, ParameterExpression row) =>
        new ParameterReplacer(selector.Parameters[0], row).Visit(selector.Body);

    private static Expression And(Expression? left, Expression right) => left is null ? right : Expression.AndAlso(left, right);

    private static Expression? BuildCondition<TItem>(Expression member, GridColumnDefinition<TItem> column, ColumnFilter filter, StringMatchMode mode)
    {
        var op = filter.Operator!.Value;
        var kind = column.Kind;

        if (op is FilterOperator.IsEmpty or FilterOperator.IsNotEmpty)
        {
            Expression isEmpty = kind == GridValueKind.Text
                ? Expression.Call(StringIsNullOrEmpty, member)
                : column.IsNullable
                    ? Expression.Equal(member, Expression.Constant(null, column.ValueType))
                    : Expression.Constant(false);
            return op == FilterOperator.IsEmpty ? isEmpty : Expression.Not(isEmpty);
        }

        if (filter.Value is null) return null;

        if (kind == GridValueKind.Text)
        {
            var value = Expression.Constant(Convert.ToString(filter.Value, CultureInfo.CurrentCulture) ?? "", typeof(string));
            var notNull = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));
            Expression test = op switch
            {
                FilterOperator.Contains or FilterOperator.NotContains => StringCall(member, value, "Contains", mode),
                FilterOperator.StartsWith => StringCall(member, value, "StartsWith", mode),
                FilterOperator.EndsWith => StringCall(member, value, "EndsWith", mode),
                FilterOperator.EqualTo or FilterOperator.NotEqualTo => mode == StringMatchMode.OrdinalIgnoreCase
                    ? Expression.Call(StringEqualsCmp, member, value, Expression.Constant(StringComparison.OrdinalIgnoreCase))
                    : Expression.Equal(member, value),
                _ => throw new NotSupportedException($"Operator {op} is not valid for text columns."),
            };
            var positive = Expression.AndAlso(notNull, test);
            return op is FilterOperator.NotContains or FilterOperator.NotEqualTo ? Expression.Not(positive) : positive;
        }

        var left = member;
        var targetType = column.ValueType;
        if (column.NonNullableType.IsEnum)
        {
            // Relational operators are not defined on enums; compare on the underlying integral type instead.
            var underlying = Enum.GetUnderlyingType(column.NonNullableType);
            targetType = column.IsNullable ? typeof(Nullable<>).MakeGenericType(underlying) : underlying;
            left = Expression.Convert(member, targetType);
        }

        var v1 = TypedConstant(filter.Value, column.NonNullableType, targetType);
        return op switch
        {
            FilterOperator.EqualTo => Expression.Equal(left, v1),
            FilterOperator.NotEqualTo => Expression.NotEqual(left, v1),
            FilterOperator.GreaterThan => Expression.GreaterThan(left, v1),
            FilterOperator.GreaterThanOrEqual => Expression.GreaterThanOrEqual(left, v1),
            FilterOperator.LessThan => Expression.LessThan(left, v1),
            FilterOperator.LessThanOrEqual => Expression.LessThanOrEqual(left, v1),
            FilterOperator.Between when filter.Value2 is not null => Expression.AndAlso(
                Expression.GreaterThanOrEqual(left, v1),
                Expression.LessThanOrEqual(left, TypedConstant(filter.Value2, column.NonNullableType, targetType))),
            FilterOperator.Between => Expression.GreaterThanOrEqual(left, v1),
            _ => throw new NotSupportedException($"Operator {op} is not valid for {kind} columns."),
        };
    }

    private static ConstantExpression TypedConstant(object raw, Type nonNullableType, Type expressionType)
    {
        var converted = ConvertValue(raw, nonNullableType)!;
        if (nonNullableType.IsEnum)
            converted = Convert.ChangeType(converted, Enum.GetUnderlyingType(nonNullableType), CultureInfo.InvariantCulture);
        return Expression.Constant(converted, expressionType);
    }

    private static Expression BuildValueSet<TItem>(Expression member, GridColumnDefinition<TItem> column, ColumnFilter filter)
    {
        var includesBlank = filter.Values.Contains(null) || (column.Kind == GridValueKind.Text && filter.Values.Contains(""));
        var concrete = filter.Values
            .Where(v => v is not null && !(v is string s && s.Length == 0))
            .Select(v => ConvertValue(v, column.NonNullableType)!)
            .ToList();

        Expression? test = null;
        if (concrete.Count > 0)
        {
            var set = MakeTypedSet(column.NonNullableType, concrete);
            var containsMethod = EnumerableContains.MakeGenericMethod(column.NonNullableType);
            var setConstant = Expression.Constant(set, typeof(IEnumerable<>).MakeGenericType(column.NonNullableType));
            if (column.ValueType != column.NonNullableType)
            {
                // Nullable<T>: row.Prop.HasValue && set.Contains(row.Prop.Value)
                test = Expression.AndAlso(
                    Expression.Property(member, "HasValue"),
                    Expression.Call(containsMethod, setConstant, Expression.Property(member, "Value")));
            }
            else
            {
                test = Expression.Call(containsMethod, setConstant, member);
            }
        }

        if (includesBlank && column.IsNullable)
        {
            Expression blank = column.Kind == GridValueKind.Text
                ? Expression.Call(StringIsNullOrEmpty, member)
                : Expression.Equal(member, Expression.Constant(null, column.ValueType));
            test = test is null ? blank : Expression.OrElse(test, blank);
        }

        test ??= Expression.Constant(false);
        return filter.ValueSetMode == ValueSetMode.Include ? test : Expression.Not(test);
    }

    private static object MakeTypedSet(Type elementType, IEnumerable<object> values)
    {
        var set = (IEnumerable)Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(elementType))!;
        var add = set.GetType().GetMethod("Add")!;
        foreach (var v in values) add.Invoke(set, [v]);
        return set;
    }

    private static Expression StringCall(Expression member, Expression value, string method, StringMatchMode mode)
    {
        if (mode == StringMatchMode.OrdinalIgnoreCase)
        {
            var mi = method switch { "Contains" => StringContainsCmp, "StartsWith" => StringStartsWithCmp, _ => StringEndsWithCmp };
            return Expression.Call(member, mi, value, Expression.Constant(StringComparison.OrdinalIgnoreCase));
        }
        var plain = method switch { "Contains" => StringContains, "StartsWith" => StringStartsWith, _ => StringEndsWith };
        return Expression.Call(member, plain, value);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
