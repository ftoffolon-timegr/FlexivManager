using FlexivManager.Hosting;

// One process, two web hosts:
//  - operator host (Hosts:Operator, loopback only): pages with commands, owns the robot worker;
//  - monitoring host (Hosts:Monitoring, intranet): read-only pages and /api, started by the operator host.
// The robot backend is the mock for now: no real robot is ever contacted.
var app = GatewayComposition.BuildOperatorApp(args);
app.Run();
