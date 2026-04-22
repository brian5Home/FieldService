using FieldService.Client;
using FieldService.Client.Data;
using FieldService.Client.Data.Migrations;
using FieldService.Client.Sync;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Required for Microsoft.Data.Sqlite to bind the e_sqlite3 provider in WASM.
Batteries_V2.Init();

// Use a browser-safe local SQLite file path.
// Absolute filesystem paths like /database/... are not valid in WASM runtime.
builder.Services.AddDbContextFactory<LocalDbContext>(o =>
    o.UseSqlite("Data Source=fieldservice.db"));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<LocalDbContext>>().CreateDbContext());

// Schema migrations — ordered by Version.
builder.Services.AddSingleton<ILocalMigration, M001_InitialSchema>();
builder.Services.AddSingleton<ILocalMigration, M002_AddPriorityToWorkOrder>();
builder.Services.AddScoped<LocalMigrationRunner>();

// App HTTP client goes to the server origin.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddScoped<WorkOrderRepository>();
builder.Services.AddSingleton<ISyncService, SyncService>();

var app = builder.Build();

// Run pending local DB migrations BEFORE any component touches the DB.
using (var scope = app.Services.CreateScope())
{
    var runner = scope.ServiceProvider.GetRequiredService<LocalMigrationRunner>();
    await runner.EnsureUpgradedAsync();
}

// Start the background sync loop after migrations are applied.
app.Services.GetRequiredService<ISyncService>().Start();

await app.RunAsync();
