using HCMSys;
using HCMSys.Models;
using HCMSys.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Configure AppConfiguration to disable reloadOnChange in production (prevents inotify resource limit on Render)
var envName = builder.Environment.EnvironmentName;
var isDev = builder.Environment.IsDevelopment();
builder.Configuration.Sources.Clear();
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: isDev)
                     .AddJsonFile($"appsettings.{envName}.json", optional: true, reloadOnChange: isDev)
                     .AddEnvironmentVariables();

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// Add services to the container
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IHcmsApiService, HcmsApiService>();
builder.Services.AddHttpClient<IJibbleApiService, JibbleApiService>();

builder.Services.AddAuthorization();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(1); // Set session timeout to 1 hour
    options.Cookie.HttpOnly = true; // Prevent client-side script access to the cookie
    options.Cookie.IsEssential = true; // Make the session cookie essential
});

var app = builder.Build();

// Configure middleware
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage(); // Show exception details in development
}
else
{
    app.UseExceptionHandler("/Home/Error"); // Standard error handling
    app.UseHsts(); // Use HTTP Strict Transport Security
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSession();
app.UseRouting();
app.UseAuthentication(); // Important: Authentication before Authorization
app.UseAuthorization();

// Custom middleware for cache control
app.Use(async (context, next) =>
{
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0"; // Expired immediately
    await next();
});

// Map default routes
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}"
);

app.MapRazorPages(); // Map Razor pages

app.Run(); // Start the application
