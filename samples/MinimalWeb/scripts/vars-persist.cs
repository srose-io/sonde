// Vars live for the life of the process: read what an earlier eval stored.
return g.Vars.TryGetValue("marker", out var marker) ? marker : "nothing stored";
