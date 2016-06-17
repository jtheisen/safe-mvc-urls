using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace IronStone.Web.Mvc.Tests
{
    [TestClass]
    public class UnitTests
    {
        [TestMethod]
        public void RunAllTests()
        {
            Tests.RunTests();
        }
    }
}
