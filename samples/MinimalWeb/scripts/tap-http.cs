// Tap HttpClient, make a request through the app's own IHttpClientFactory, read the tap.
g.Tap("HttpHandlerDiagnosticListener");
// CreateClient() with no name is an extension method in a namespace the script does not import; the default client is the empty name.
var client = g.Resolve<System.Net.Http.IHttpClientFactory>().CreateClient(string.Empty);
var body = await client.GetStringAsync(new Uri((string)g.Vars["baseUrl"]!), ct);
var events = g.Tapped("HttpHandlerDiagnosticListener");
g.Untap("HttpHandlerDiagnosticListener");
return new { body, eventNames = events.Select(e => e.EventName).Distinct().ToArray() };
