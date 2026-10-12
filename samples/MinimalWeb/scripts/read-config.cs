// Read a configuration value through the host's own IConfiguration.
var config = g.Resolve<Microsoft.Extensions.Configuration.IConfiguration>();
return config["Greeting"];
