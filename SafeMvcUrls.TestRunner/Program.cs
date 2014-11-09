using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SafeMvcUrls.TestRunner
{
    class Program
    {
        static void Main(string[] args)
        {
            MonkeyBusters.Web.Mvc.Tests.Tests.RunTests();

            Console.WriteLine("All fine.");

            Console.ReadKey();
        }
    }
}
