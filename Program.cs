using MongoTestTools.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromHours(24);
})
.AddHubOptions(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromHours(24);
    options.KeepAliveInterval = TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<ChangeStreamMonitorService>();
builder.Services.AddSingleton<ChangeStreamGeneratorService>();
builder.Services.AddSingleton<CollectionComparerService>();
builder.Services.AddScoped<AuthService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
