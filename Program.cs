using ServerGrid.Components;
using ServerGrid.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Keep the diff pipeline flowing smoothly while a user scrolls quickly.
        options.MaxBufferedUnacknowledgedRenderBatches = 20;
    });

builder.Services.AddSingleton<TradeRepository>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Warm the data set up so the first visitor doesn't wait for generation.
_ = Task.Run(() => app.Services.GetRequiredService<TradeRepository>().Trades);

app.Run();
