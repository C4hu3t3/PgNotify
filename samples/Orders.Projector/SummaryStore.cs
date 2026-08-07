using System.Collections.Concurrent;
using Orders.Model;

namespace Orders.Projector;

/// <summary>
/// The read-model: a per-customer order count and running total, kept in memory and rebuilt from
/// whatever rows notifications have named so far. Deliberately holds full <see cref="Order"/>
/// snapshots rather than incrementally maintained totals, so an update simply overwrites the
/// previous snapshot instead of needing to know what changed.
/// </summary>
public sealed class SummaryStore
{
    private readonly ConcurrentDictionary<int, Order> _orders = new();

    public void Upsert(Order order) => _orders[order.Id] = order;

    public void Remove(int id) => _orders.TryRemove(id, out _);

    public IReadOnlyList<CustomerSummary> Summarize() =>
        _orders.Values
            .GroupBy(o => o.CustomerName)
            .Select(g => new CustomerSummary(g.Key, g.Count(), g.Sum(o => o.Amount)))
            .OrderByDescending(s => s.Total)
            .ToList();
}

public sealed record CustomerSummary(string CustomerName, int OrderCount, decimal Total);
