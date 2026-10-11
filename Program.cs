using FlexivManager.Components;
using Microsoft.Extensions.Options;
using RobotGateway.Core;
using RobotGateway.Mock;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Robot gateway. Only the mock backend exists for now: no real robot is ever contacted.
builder.Services.AddOptions<RobotWorkerOptions>()
    .Bind(builder.Configuration.GetSection(RobotWorkerOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<MockBackendOptions>()
    .Bind(builder.Configuration.GetSection(MockBackendOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new MockBackend(
    sp.GetRequiredService<IOptions<MockBackendOptions>>().Value,
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IRobotBackend>(sp => sp.GetRequiredService<MockBackend>());
builder.Services.AddSingleton<SnapshotStore>();
builder.Services.AddSingleton(_ => new EventLog());
builder.Services.AddSingleton<RobotCommandService>();
builder.Services.AddHostedService<RobotWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
