using ArctisAurora.Core.Diagnostics;
using ArctisAurora.Core.Filing.Arx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace _Build
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if ((args.Length == 2 || args.Length == 3) && args[0] == "arx")
            {
                bool built = AppBuilder.Run(args[1], args.Length == 3 ? args[2] : null);
                LogSpool.Flush();
                return built ? 0 : 1;
            }
            if (args.Length >= 2 && args[0] == "codedocs") return CodeDocs.Run(args);
            return 0;
        }
    }
}
