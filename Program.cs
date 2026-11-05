using FlexivManager.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

// Utility: FlexivManager hash-password <password>  -> value for Hosts:Monitoring:Editor:PasswordHash
if (args is ["hash-password", var password])
{
    Console.WriteLine(PasswordHasher.Hash(password));
    return;
}

// Started by the Service Control Manager: the working directory is C:\Windows\System32, so make the
// install folder the content root (appsettings*.json, data\, relative ERP folder are resolved from it).
if (WindowsServiceHelpers.IsWindowsService())
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// One process, two web hosts:
//  - operator host (Hosts:Operator, loopback only): local station, runs jobs, owns the robot worker;
//  - monitoring host (Hosts:Monitoring, intranet): monitoring + planning (jobs, work plan) behind login, REST write API behind an API key.
// Robot:Backend selects the simulator (Mock) or the real robot (Flexiv).
// AddWindowsService: start/stop driven by the Service Control Manager when installed as a Windows service
// (installer/install.ps1), no effect when started from the console or Visual Studio.
var app = GatewayComposition.BuildOperatorApp(args, builder =>
    builder.Services.AddWindowsService(o => o.ServiceName = "FlexivManager"));
app.Run();
