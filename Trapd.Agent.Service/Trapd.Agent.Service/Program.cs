using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Trapd.Agent.Service;

var builder = Host.CreateApplicationBuilder(args);

// Logging (Datei machst du selbst in Worker, aber Debug ist praktisch)
builder.Logging.ClearProviders();
builder.Logging.AddEventLog();
builder.Logging.AddConsole(); // für manuelles Testen in PowerShell

builder.Services.AddHostedService<Worker>();

// ✅ Nur als Dienst WindowsServiceLifetime aktivieren
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "TRAPD Agent";
    });
}

var app = builder.Build();
await app.RunAsync();
