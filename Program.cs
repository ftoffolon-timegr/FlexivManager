using FlexivManager.Hosting;

// Utility: FlexivManager hash-password <password>  -> value for Hosts:Monitoring:Editor:PasswordHash
if (args is ["hash-password", var password])
{
    Console.WriteLine(PasswordHasher.Hash(password));
    return;
}

// One process, two web hosts:
//  - operator host (Hosts:Operator, loopback only): local station, runs jobs, owns the robot worker;
//  - monitoring host (Hosts:Monitoring, intranet): monitoring + planning (jobs, work list) behind login.
// The robot backend is the mock for now: no real robot is ever contacted.
var app = GatewayComposition.BuildOperatorApp(args);
app.Run();
