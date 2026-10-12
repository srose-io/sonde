// The worker has no ASP.NET, so the DI root came from the container's constructor, not a request.
var environment = g.Resolve<Microsoft.Extensions.Hosting.IHostEnvironment>();
return new { environment.ApplicationName, environment.EnvironmentName };
