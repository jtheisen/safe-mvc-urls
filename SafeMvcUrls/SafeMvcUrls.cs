using Castle.DynamicProxy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Mvc;
using System.Web.Routing;

namespace MonkeyBusters.Web.Mvc
{
    #region The stringigyable return values

    class StringifiableResult : ActionResult
    {
        public StringifiableResult(String txt)
        {
            this.txt = txt;
        }

        public override String ToString()
        {
            return txt;
        }

        public override void ExecuteResult(ControllerContext context)
        {
            throw new NotImplementedException("This is a pseudo-result that isn't supposed to be executed.");
        }

        String txt;
    }

    class StringifiableResultTask : Task<ActionResult>
    {
        public StringifiableResultTask(String txt)
            : base(Impl)
        {
            this.txt = txt;
        }

        public override String ToString()
        {
            return txt;
        }

        static ActionResult Impl()
        {
            throw new NotImplementedException("This is a pseudo-action that isn't supposed to be executed.");
        }

        String txt;
    }

    #endregion

    #region The controller nanny

    public class ControllerProblem
    {
        public ControllerProblem(MethodInfo methodInfo, String message)
        {
            MethodInfo = methodInfo;
            Message = message;
        }

        public override string ToString()
        {
            return String.Format("About {0}.{1}: {2}", MethodInfo.DeclaringType.Name, MethodInfo.Name, Message);
        }

        public MethodInfo MethodInfo { get; private set; }
        public String Message { get; private set; }
    }

    public class ControllerProblemsException : Exception
    {
        public ControllerProblemsException(ControllerProblem[] problems)
            : base("There are problems with your controllers:\n" + GetReport(problems))
        {
            this.problems = problems;
        }

        static String GetReport(ControllerProblem[] problems)
        {
            return String.Join("\n", problems.Select(p => p.ToString()));
        }

        public ControllerProblem[] Problems { get { return problems; } }

        ControllerProblem[] problems;
    }

    static class ControllerNanny
    {
        static readonly Type[] IgnoredDelcaringTypes = new[] { typeof(Object), typeof(ControllerBase), typeof(Controller), typeof(AsyncController) };

        internal static void Assert(Type type)
        {
            var problems = Check(type).ToArray();

            if (problems.Length > 0)
            {
                throw new ControllerProblemsException(problems);
            }
        }

        internal static IEnumerable<ControllerProblem> Check(Type type)
        {
            var descriptor = ControllerDescriptorCache.Create(type);

            var actionDescriptors = descriptor.GetActionDescriptors();

            return
                from ad in actionDescriptors.OfType<ReflectedActionDescriptor>()
                from p in Check(ad.MethodInfo)
                select p;
        }

        static IEnumerable<ControllerProblem> Check(MethodInfo info)
        {
            // We're a bit lenient on void return values as presumably the user will figure out on his own that he can't
            // convert the result value into a string: Both in the view and in controller contexts it will give compile
            // time errors on standard usage.
            if (info.ReturnType == typeof(void)) yield break;

            // One would think that methods marked as not an actions are not returned by the
            // ReflectedControllerDescriptor anyway. Alas, they are...
            if (info.GetCustomAttribute<NonActionAttribute>() != null) yield break;

            if (!ActionUrlCreatingInterceptor.IsAcceptableAsReturnType(info.ReturnType))
            {
                yield return new ControllerProblem(info, "Method has neither ActionResult nor Task<ActionResult> as a return type and will not be overriden in a fake proxy.");
            }

            if (!info.IsVirtual)
            {
                yield return new ControllerProblem(info, "The action method is not marked as virtual an can not be overridden in a fake proxy.");
            }
        }
    }

    #endregion

    #region The controller descriptor cache

    abstract class ControllerDescriptorCache
    {
        public static ControllerDescriptorCache Create(Type controllerType)
        {
            var cacheType = typeof(ControllerDescriptorCache<>).MakeGenericType(controllerType);

            return Activator.CreateInstance(cacheType) as ControllerDescriptorCache;
        }

        public abstract ControllerDescriptor GetControllerDescriptor();

        public abstract ActionDescriptor[] GetActionDescriptors();
    }

    class ControllerDescriptorCache<T> : ControllerDescriptorCache
        where T : IController
    {
        public override ControllerDescriptor GetControllerDescriptor() { return controllerDescriptor; }

        public override ActionDescriptor[] GetActionDescriptors() { return actionDescriptors; }

        static readonly ControllerDescriptor controllerDescriptor = new ReflectedControllerDescriptor(typeof(T));

        static readonly ActionDescriptor[] actionDescriptors = controllerDescriptor.GetCanonicalActions();
    }

    #endregion

    #region The IInterceptor implementation doing the main work

    class ActionUrlCreatingInterceptor : IInterceptor
    {
        public ActionUrlCreatingInterceptor(Type controllerType, UrlHelper urlHelper, RouteValueDictionary values = null, String protocol = null, String hostname = null)
        {
            this.controllerType = controllerType;
            this.urlHelper = urlHelper;
            this.values = values;
            this.protocol = protocol;
            this.hostname = hostname;
        }

        public void Intercept(IInvocation invocation)
        {
            var returnType = invocation.Method.ReturnType;

            if (!IsAcceptableAsReturnType(returnType))
            {
                throw new Exception(String.Format("You called the controller method {0} that doesn't return an ActionResult on the helper returned by one of the *To() overloads.", invocation.Method));
            }

            if (invocation.Method.GetCustomAttribute<NonActionAttribute>() != null)
            {
                throw new Exception(String.Format("You called a controller method {0} that is marked as not being an action on the helper returned by one of the *To() overloads.", invocation.Method));
            }

            var descriptors = ControllerDescriptorCache.Create(controllerType);

            var controllerDescriptor = descriptors.GetControllerDescriptor();

            var actionDescriptor = controllerDescriptor.GetCanonicalActions()
                .OfType<ReflectedActionDescriptor>().FirstOrDefault(ad => ad.MethodInfo == invocation.Method);

            if (actionDescriptor == null) throw new Exception(String.Format("You called a controller method {0} which MVC thinks is not an action on the helper returned by one of the *To() overloads.", invocation.Method));

            // Strange that ActionDescriptor.ActionName doesn't always return the action's name...
            var actionNameAttribute = actionDescriptor.MethodInfo.GetCustomAttribute<ActionNameAttribute>();
            var actionName = actionNameAttribute != null ? actionNameAttribute.Name : actionDescriptor.ActionName;

            // as it is the case with controllers.
            var controllerName = controllerDescriptor.ControllerName;

            var values = new RouteValueDictionary();

            if (this.values != null)
            {
                foreach (var kvp in this.values)
                {
                    values[kvp.Key] = kvp.Value;
                }
            }

            var parameters = invocation.Method.GetParameters();

            for (var i = 0; i < parameters.Length; ++i)
            {
                var p = parameters[i];
                var a = invocation.Arguments[i];

                if (!p.IsOptional || !AreEqual(p.ParameterType, a, p.DefaultValue))
                {
                    values[parameters[i].Name] = invocation.Arguments[i];
                }
            }

            var url = urlHelper.Action(actionName, controllerName, values, protocol, hostname);

            if (returnType.IsGenericType)
            {
                invocation.ReturnValue = new StringifiableResultTask(url);
            }
            else
            {
                invocation.ReturnValue = new StringifiableResult(url);
            }
        }

        internal static Boolean IsAcceptableAsReturnType(Type type)
        {
            if (type == typeof(ActionResult)) return true;
            if (type == typeof(Task<ActionResult>)) return true;
            return false;
        }

        static Boolean AreEqual(Type type, Object argument, Object defaultValue)
        {
            if (argument == null) return defaultValue == null;

            // For some bizarre reason, a default of "default(Guid)" leads to *null* as reflected default value.
            // That's not the case with Int32, so I'm a bit at a loss as to what's different with Guid.
            // Anyway, this is the special handling to make Guids behave properly. Non-nullable type's can't be
            // null, so that case is properly identifiable.
            if (type.IsValueType && defaultValue == null) return argument.Equals(Activator.CreateInstance(type));

            return argument.Equals(defaultValue);
        }

        Type controllerType;
        UrlHelper urlHelper;
        RouteValueDictionary values;
        String protocol;
        String hostname;
    }

    #endregion

    #region The public overloads

    /// <summary>
    /// Extensions for the creation of urls to actions in a name- and type-safe manner.
    /// </summary>
    public static class SafeMvcUrls
    {
        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns>The fake proxy controller.</returns>
        public static C To<C>(this UrlHelper urlHelper, String protocol = null, String hostname = null)
            where C : Controller
        {
            ControllerNanny.Assert(typeof(C));

            return generator.CreateClassProxy(typeof(C), new ActionUrlCreatingInterceptor(typeof(C), urlHelper, null, protocol, hostname)) as C;
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="routeValues">Optional route values. Those can be overridden by what is derived from the action call.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns></returns>
        public static C To<C>(this UrlHelper urlHelper, RouteValueDictionary routeValues, String protocol = null, String hostname = null)
            where C : Controller
        {
            ControllerNanny.Assert(typeof(C));

            var interceptor = new ActionUrlCreatingInterceptor(typeof(C), urlHelper, routeValues, protocol, hostname);

            return generator.CreateClassProxy(typeof(C), interceptor) as C;
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="routeValues">Optional route values. Those can be overridden by what is derived from the action call.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns></returns>
        public static C To<C>(this UrlHelper urlHelper, Object routeValues, String protocol = null, String hostname = null)
            where C : Controller
        {
            ControllerNanny.Assert(typeof(C));

            var dict = new RouteValueDictionary(routeValues);

            var interceptor = new ActionUrlCreatingInterceptor(typeof(C), urlHelper, dict, protocol, hostname);

            return generator.CreateClassProxy(typeof(C), interceptor) as C;
        }

        static readonly ProxyGenerator generator = new ProxyGenerator();
    }

    #endregion

    #region Tests

    namespace Tests
    {
        using System.Web;
        using NameValueCollection = System.Collections.Specialized.NameValueCollection;

        public class GoodController : Controller
        {
            public virtual ActionResult Trivial() { return View(); }


            public virtual ActionResult Simple(String s) { return View(); }

            public virtual ActionResult Simple(Int32 i) { return View(); }

            public virtual ActionResult Simple(Guid g) { return View(); }


            public virtual ActionResult WithDefaultString(String s = "default") { return View(); }

            public virtual ActionResult WithDefaultInt32(Int32 i = -1) { return View(); }

            public virtual ActionResult WithDefaultGuid(Guid g = default(Guid)) { return View(); }


            public virtual ActionResult NamedParams(String a = "a", String b = "b") { return View(); }


            [ActionName("explicitly-named")]
            public virtual ActionResult ExplicitlyNamed() { return View(); }

            public virtual Task<ActionResult> Asyncy() { return null; }

            public virtual Task<ActionResult> Asyncy(String s) { return null; }


            // The nanny will allow non-virtual non-actions.
            [NonAction]
            public ActionResult NoAction() { return View(); }

            // The nanny will allow void results.
            public virtual void VoidResult() { }

            // The nanny will allow non-public methods with arbitrary results.
            protected virtual ViewResult ProtectedViewResult() { return View(); }
        }

        public class BadController : Controller
        {
            public ActionResult NonVirtual() { return View(); }

            public virtual ViewResult ViewResult() { return View(); }

            public virtual Task<ViewResult> TaskViewResult() { return null; }
        }

        class MockResponse : HttpResponseBase
        {
            public override string ApplyAppPathModifier(String virtualPath)
            {
                return virtualPath;
            }
        }

        class MockRequest : HttpRequestBase
        {
            public override string ApplicationPath { get { return "/"; } }

            public override Uri Url { get { return new Uri("http://www.example.com", UriKind.Absolute); } }

            public override System.Collections.Specialized.NameValueCollection ServerVariables { get { return nvc; } }

            NameValueCollection nvc = new NameValueCollection();
        }

        class MockContext : HttpContextBase
        {
            public override HttpRequestBase Request { get { return request; } }

            public override HttpResponseBase Response { get { return response; } }

            MockRequest request = new MockRequest();
            MockResponse response = new MockResponse();
        }

        public class Tests
        {
            public static void RunTests()
            {
                var tests = new Tests();
                tests.Test();
            }

            public Tests()
            {
                var routes = new RouteCollection();

                routes.MapRoute(
                    name: "Default",
                    url: "{controller}/{action}"
                );

                url = new UrlHelper(new RequestContext(new MockContext(), new RouteData()), routes);
            }

            void Test()
            {
                AssertEqual(Url.To<GoodController>().Trivial(), "/Good/Trivial");

                AssertEqual(Url.To<GoodController>(protocol: "https").Trivial(), "https://www.example.com/Good/Trivial");
                AssertEqual(Url.To<GoodController>(hostname: "localhost").Trivial(), "http://localhost/Good/Trivial");

                AssertEqual(Url.To<GoodController>().Simple("foo"), "/Good/Simple?s=foo");
                AssertEqual(Url.To<GoodController>().Simple(42), "/Good/Simple?i=42");
                AssertEqual(Url.To<GoodController>().Simple(someGuid), "/Good/Simple?g=" + someGuid);

                AssertEqual(Url.To<GoodController>().WithDefaultString("foo"), "/Good/WithDefaultString?s=foo");
                AssertEqual(Url.To<GoodController>().WithDefaultString("default"), "/Good/WithDefaultString");
                AssertEqual(Url.To<GoodController>().WithDefaultString(), "/Good/WithDefaultString");

                AssertEqual(Url.To<GoodController>().WithDefaultInt32(42), "/Good/WithDefaultInt32?i=42");
                AssertEqual(Url.To<GoodController>().WithDefaultInt32(-1), "/Good/WithDefaultInt32");
                AssertEqual(Url.To<GoodController>().WithDefaultInt32(), "/Good/WithDefaultInt32");

                AssertEqual(Url.To<GoodController>().WithDefaultGuid(), "/Good/WithDefaultGuid");
                AssertEqual(Url.To<GoodController>().WithDefaultGuid(Guid.Empty), "/Good/WithDefaultGuid");
                AssertEqual(Url.To<GoodController>().WithDefaultGuid(someGuid), "/Good/WithDefaultGuid?g=" + someGuid);

                AssertEqual(Url.To<GoodController>().WithDefaultString(), "/Good/WithDefaultString");
                AssertEqual(Url.To<GoodController>(new { x = "bar" }).WithDefaultString("foo"), "/Good/WithDefaultString?x=bar&s=foo");
                AssertEqual(Url.To<GoodController>(new { s = "bar" }).WithDefaultString("foo"), "/Good/WithDefaultString?s=foo");
                AssertEqual(Url.To<GoodController>(new { s = "bar" }).WithDefaultString(), "/Good/WithDefaultString?s=bar");
                AssertEqual(Url.To<GoodController>(new { s = "default" }).WithDefaultString(), "/Good/WithDefaultString?s=default");

                AssertEqual(Url.To<GoodController>().NamedParams(b: "x"), "/Good/NamedParams?b=x");

                AssertEqual(Url.To<GoodController>().ExplicitlyNamed(), "/Good/explicitly-named");
                AssertEqual(Url.To<GoodController>().Asyncy(), "/Good/Asyncy");
                AssertEqual(Url.To<GoodController>().Asyncy("foo"), "/Good/Asyncy?s=foo");

                try
                {
                    Url.To<BadController>();
                }
                catch (ControllerProblemsException ex)
                {
                    var expectedProblems = 3;

                    if (ex.Problems.Length != expectedProblems)
                    {
                        throw new Exception(String.Format("Expected {0} problems in the bad controller, but got {1}.", expectedProblems, ex.Problems.Length));
                    }
                }
            }

            void AssertEqual(Object result, String expectation)
            {
                if (result.ToString() != expectation)
                {
                    throw new Exception(String.Format("Test failure: Expected {0}, but got {1}.", expectation, result));
                }
            }

            static readonly Guid someGuid = Guid.NewGuid();

            private UrlHelper Url { get { return url; } }

            UrlHelper url;
        }
    }

    #endregion
}
