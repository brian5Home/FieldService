using FieldService.Server.Data;
using FieldService.Server.Sync;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("Default")
                  ?? "Data Source=fieldservice.db"));

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("https://localhost:5001", "http://localhost:5000")
    .AllowAnyHeader()
    .AllowAnyMethod()));

builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// Apply migrations + seed on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await Seed.RunAsync(db);
}

app.UseCors();
app.MapSyncEndpoints();

// Tiny UI-facing read endpoints would go here (work order list for a technician, etc.).
// They are not needed for the offline path because the client reads from its own SQLite.

app.Run();
