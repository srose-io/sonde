// Resolve a singleton and read its private field by reflection.
var counter = g.Resolve<MinimalWeb.CounterService>();
var field = typeof(MinimalWeb.CounterService).GetField("_count", BindingFlags.NonPublic | BindingFlags.Instance)!;
return new { Count = (int)field.GetValue(counter)!, Type = counter.GetType().FullName };
