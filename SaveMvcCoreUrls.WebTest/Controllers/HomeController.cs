using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using IronStone.Web.Mvc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SaveMvcCoreUrls.WebTest.Models;

namespace SaveMvcCoreUrls.WebTest.Controllers
{
    public class HomeController : Controller
    {
        public String Name { get; set; }

        private readonly ILogger<HomeController> _logger;

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public virtual IActionResult Index()
        {
            return View();
        }

        [HttpGet("privacy")]
        public virtual IActionResult Privacy()
        {
            return View();
        }

        [HttpGet("foo/{x}")]
        public virtual IActionResult Foo(Int32 x)
        {
            return View();
        }

        public class BarNestedParam
        {
            public String Z { get; set; }
        }

        [MvcParameterAggregate]
        public class BarParams
        {
            public String X { get; set; }
            public String Y { get; set; }
        }

        [HttpGet("bar")]
        public virtual IActionResult Bar(BarParams model)
        {
            return Content($"X={model.X}, Y={model.Y}", "text/plain");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public virtual IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
