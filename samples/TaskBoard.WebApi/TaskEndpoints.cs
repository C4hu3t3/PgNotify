using Microsoft.EntityFrameworkCore;
using TaskBoard.Model;

namespace TaskBoard.WebApi;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tasks");

        group.MapGet("/", async (SampleDbContext db) =>
            await db.Tasks.AsNoTracking().OrderBy(t => t.Id).ToListAsync());

        group.MapPost("/", async (TaskRequest request, SampleDbContext db) =>
        {
            var task = new TaskItem { Title = request.Title, IsDone = request.IsDone };
            db.Tasks.Add(task);
            await db.SaveChangesAsync();
            return Results.Created($"/api/tasks/{task.Id}", task);
        });

        group.MapPut("/{id:int}", async (int id, TaskRequest request, SampleDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null)
            {
                return Results.NotFound();
            }

            task.Title = request.Title;
            task.IsDone = request.IsDone;
            await db.SaveChangesAsync();
            return Results.Ok(task);
        });

        group.MapDelete("/{id:int}", async (int id, SampleDbContext db) =>
        {
            var task = await db.Tasks.FindAsync(id);
            if (task is null)
            {
                return Results.NotFound();
            }

            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public sealed record TaskRequest(string Title, bool IsDone);
