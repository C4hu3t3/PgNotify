using Microsoft.EntityFrameworkCore;
using WelcomeEmail.Model;

namespace WelcomeEmail.WebApi;

public static class MemberEndpoints
{
    public static void MapMemberEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/members");

        group.MapGet("/", async (SampleDbContext db) =>
            await db.Members.AsNoTracking().OrderByDescending(m => m.Id).ToListAsync());

        // Sign-up. This handler does nothing about email at all - it writes a row and returns.
        // The welcome email is WelcomeEmail.Mailer's job, triggered by the database's own INSERT
        // notification, so the request path stays free of SMTP latency and SMTP failures, and it
        // works identically no matter how many instances of this API are running.
        group.MapPost("/", async (SignUpRequest request, SampleDbContext db) =>
        {
            var member = new Member { Email = request.Email, DisplayName = request.DisplayName };
            db.Members.Add(member);
            await db.SaveChangesAsync();
            return Results.Created($"/api/members/{member.Id}", member);
        });
    }
}

public sealed record SignUpRequest(string Email, string DisplayName);
