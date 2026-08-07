using Microsoft.EntityFrameworkCore;
using Orders.Model;

namespace Orders.WebApi;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orders");

        group.MapGet("/", async (SampleDbContext db) =>
            await db.Orders.AsNoTracking().OrderByDescending(o => o.Id).ToListAsync());

        group.MapPost("/", async (OrderRequest request, SampleDbContext db) =>
        {
            var order = new Order { CustomerName = request.CustomerName, Amount = request.Amount };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            return Results.Created($"/api/orders/{order.Id}", order);
        });

        group.MapPut("/{id:int}", async (int id, OrderRequest request, SampleDbContext db) =>
        {
            var order = await db.Orders.FindAsync(id);
            if (order is null)
            {
                return Results.NotFound();
            }

            order.CustomerName = request.CustomerName;
            order.Amount = request.Amount;
            await db.SaveChangesAsync();
            return Results.Ok(order);
        });

        group.MapDelete("/{id:int}", async (int id, SampleDbContext db) =>
        {
            var order = await db.Orders.FindAsync(id);
            if (order is null)
            {
                return Results.NotFound();
            }

            db.Orders.Remove(order);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public sealed record OrderRequest(string CustomerName, decimal Amount);
