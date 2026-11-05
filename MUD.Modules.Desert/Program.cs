using System;
using System.Threading.Tasks;
using MUD.Modules.Sdk;

namespace MUD.Modules.Desert
{
    public static class Program
    {
        // Core starts this program from modules/desert/desert (D67). `--describe` prints the description and exits.
        public static Task<int> Main(string[] args)
        {
            string problem;
            DesertSettings settings = DesertSettings.Load(AppContext.BaseDirectory, out problem);
            if (problem != null) { Console.WriteLine(problem); }
            return ModuleRunner.RunProcessAsync(DesertModule.Define(new DesertModule(settings)), args);
        }
    }
}
