# The README recording. Render with: nix-shell -p vhs --run "vhs docs/assets/demo.tape"
# (or `vhs docs/assets/demo.tape` with vhs installed). Expects `sonde` on PATH.
Output docs/assets/demo.gif
Set Shell bash
Set FontSize 15
Set Width 1000
Set Height 520
Set Theme "Catppuccin Mocha"
Set TypingSpeed 35ms
Set Padding 18

Type `sonde run --name web -- dotnet run --project samples/MinimalWeb &`
Enter
Sleep 14s
Type "clear"
Enter
Type `sonde eval web 'return g.Resolve<Microsoft.Extensions.Configuration.IConfiguration>()["Greeting"];'`
Enter
Sleep 2.5s
Type `sonde eval web @samples/MinimalWeb/scripts/hook-method.cs`
Enter
Sleep 3.5s
Type `sonde eval web 'g.Vars["n"] = 41; return "stored";'`
Enter
Sleep 2s
Type `sonde eval web 'return (int)g.Vars["n"] + 1;'`
Enter
Sleep 3s
