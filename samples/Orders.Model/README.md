# Orders.Model

The one thing [`Orders.WebApi`](../Orders.WebApi) and [`Orders.Projector`](../Orders.Projector)
share: `Order.cs`, a plain entity with `[NotifyChanges]` and `[Table("Order")]`. Nothing else — no
EF Core, no `Npgsql.EntityFrameworkCore.PostgreSQL`, no ASP.NET Core.

Unlike [`TaskBoard.Model`](../TaskBoard.Model), `[Table("Order")]` here isn't compensating for a
process with no `DbContext` at all — both `Orders.WebApi` and `Orders.Projector` have their own.
It's compensating for a different risk: two *independent* `DbContext` types, in two different
projects, each deriving a table/channel name from EF Core's default convention (which keys off the
`DbSet` property name). Nothing stops them from disagreeing — `DbSet<Order> Orders` on one side and
`DbSet<Order> OrderEntities` on the other would silently produce two different channel names, and
the projector would never receive anything, with no error anywhere. Pinning the table name in the
shared model removes that coordination risk at the source, regardless of what either side calls its
`DbSet`.

See [`samples/README.md`](../README.md#orderswebapi--ordersprojector--fully-implemented) for the
full picture.
