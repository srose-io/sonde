// Postfix the counter's Increment so it returns a thousand more than it would, call it, then unhook.
var counter = g.Resolve<MinimalWeb.CounterService>();
var before = counter.Increment();
var handle = g.Hook<MinimalWeb.CounterService>("Increment", postfix: c => c.Result = (int)c.Result! + 1000);
var hooked = counter.Increment();
g.Unhook(handle);
var after = counter.Increment();
return new { before, hooked, after, description = handle.Description };
