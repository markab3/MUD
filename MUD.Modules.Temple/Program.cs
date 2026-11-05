using System.Threading.Tasks;
using MUD.Modules.Sdk;

namespace MUD.Modules.Temple
{
    public static class Program
    {
        // Core starts this program from modules/temple/temple (D67). `--describe` prints the description and exits.
        public static Task<int> Main(string[] args)
        {
            return ModuleRunner.RunProcessAsync(TempleModule.Define(), args);
        }
    }
}
