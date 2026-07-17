// Dumps PICASSO's flat byte layout for one or more copybooks, as TSV, for the
// GnuCOBOL differential oracle (see ../README.md). Output per file:
//   ## <path> <recordLen>          (or "## <path> ERROR <message>")
//   <fieldName>\t<start>\t<len>\t<type>
using Picasso.Core;
using Picasso.Core.Models;

if (args.Length == 0) { Console.Error.WriteLine("usage: picasso-layout <copybook>..."); return 2; }
foreach (var path in args)
{
    string src;
    try { src = File.ReadAllText(path); }
    catch (Exception e) { Console.WriteLine($"## {path} ERROR read: {e.Message}"); continue; }
    try
    {
        var flat = CopybookParser.Parse(src).Flat;
        int total = flat.Count == 0 ? 0 : flat.Max(f => f.Start + f.Len);
        Console.WriteLine($"## {path} {total}");
        foreach (var f in flat)
            Console.WriteLine($"{f.Name}\t{f.Start}\t{f.Len}\t{f.Type}");
    }
    catch (Exception e) { Console.WriteLine($"## {path} ERROR {e.GetType().Name}: {e.Message}"); }
}
return 0;
