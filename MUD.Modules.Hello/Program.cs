using System.Threading.Tasks;
using MUD.Modules.Sdk;

namespace MUD.Modules.Hello
{
    public static class Program
    {
        // Core starts this program from modules/hello/hello (D67). `--describe` prints the description and exits.
        public static Task<int> Main(string[] args)
        {
            return ModuleRunner.RunProcessAsync(HelloModule.Define(), args);
        }
    }
}
