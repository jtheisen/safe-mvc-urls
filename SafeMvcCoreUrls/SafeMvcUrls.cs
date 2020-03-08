using Castle.DynamicProxy;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;


#region The controller nanny

namespace IronStone.Web.Mvc.SafeMvcUrls
{
    /// <summary>
    /// Represents a problem with a controller.
    /// </summary>
    public class ControllerProblem
    {
        internal ControllerProblem(MethodInfo methodInfo, String message)
        {
            MethodInfo = methodInfo;
            Message = message;
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>
        /// A <see cref="System.String" /> that represents this instance.
        /// </returns>
        public override string ToString()
        {
            return String.Format("About {0}.{1}: {2}", MethodInfo.DeclaringType.Name, MethodInfo.Name, Message);
        }

        /// <summary>
        /// Gets the method information for the problematic method.
        /// </summary>
        /// <value>
        /// The method information.
        /// </value>
        public MethodInfo MethodInfo { get; private set; }

        /// <summary>
        /// Gets a message explaining the problem.
        /// </summary>
        /// <value>
        /// The message.
        /// </value>
        public String Message { get; private set; }
    }

    /// <summary>
    /// An exception representing one or more problems with controllers.
    /// </summary>
    /// <seealso cref="System.Exception" />
    public class ControllerProblemsException : Exception
    {
        internal ControllerProblemsException(ControllerProblem[] problems)
            : base("There are problems with your controllers:\n" + GetReport(problems))
        {
            this.problems = problems;
        }

        static String GetReport(ControllerProblem[] problems)
        {
            return String.Join("\n", problems.Select(p => p.ToString()));
        }

        /// <summary>
        /// The problems found with controllers.
        /// </summary>
        /// <value>
        /// The problems.
        /// </value>
        public ControllerProblem[] Problems { get { return problems; } }

        ControllerProblem[] problems;
    }

    static class ControllerNanny
    {
        static readonly Type[] IgnoredDelcaringTypes = new[] { typeof(Object), typeof(ControllerBase), typeof(Controller) };

        internal static void Assert(IServiceProvider services, Type type)
        {
            var problems = Check(services, type).ToArray();

            if (problems.Length > 0)
            {
                throw new ControllerProblemsException(problems);
            }
        }

        internal static IEnumerable<ControllerProblem> Check(IServiceProvider services, Type type)
        {
            var descriptor = ControllerDescriptorCache.Get(services, type);

            var actionDescriptors = descriptor.Actions;
            
            return
                from ad in actionDescriptors.OfType<ControllerActionDescriptor>()
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

            if (info.GetCustomAttribute<NoSafeMvcUrlsAttribute>() != null) yield break;

            if (!ActionUrlCreatingInterceptor.IsAcceptableAsReturnType(info.ReturnType))
            {
                yield return new ControllerProblem(info, "Any controller you want to use one of the .To<> overloads of SafeMvcUrls on needs to have all their actions return either IActionResult or Task<IActionResult>.");
            }

            if (!info.IsVirtual)
            {
                yield return new ControllerProblem(info, "Any controller you want to use one of the .To<> overloads of SafeMvcUrls on needs to have all their actions marked as virtual.");
            }
        }
    }
}

#endregion

namespace IronStone.Web.Mvc
{
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Mvc.Infrastructure;
    using Microsoft.AspNetCore.Mvc.Routing;
    using Microsoft.AspNetCore.Mvc.ViewFeatures;
    using Microsoft.AspNetCore.Routing;
    using SafeMvcUrls;

    /// <summary>
    /// All controller implementations made by the .To&lt;C> overloads
    /// implement this interface.
    /// </summary>
    public interface ISafeMvcUrlsControllerImplementation
    {
    }

    /// <summary>
    /// Make the controller nanny ignore the annotated action and no safe urls can
    /// be created to it.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public class NoSafeMvcUrlsAttribute : Attribute
    {
    }

    #region Aggregates

    /// <summary>
    /// Makes this class be unbundled on use as arguments to the .To&lt;C> overloads.
    /// </summary>
    /// <seealso cref="System.Attribute" />
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public class MvcParameterAggregateAttribute : Attribute
    {
    }

    abstract class MvcAggregateHelper
    {
        internal static void AddValues(RouteValueDictionary dict, Type type, Object aggregate)
        {
            var helperType = typeof(MvcAggregateHelper<>).GetGenericTypeDefinition().MakeGenericType(type);

            var helper = Activator.CreateInstance(helperType) as MvcAggregateHelper;

            helper.AddValues(dict, aggregate);
        }

        protected abstract void AddValues(RouteValueDictionary dict, Object aggregate);
    }

    class MvcAggregateHelper<A> : MvcAggregateHelper
        where A : class, new()
    {
        protected override void AddValues(RouteValueDictionary dict, object untypedAggregate)
        {
            var typedAggregate = untypedAggregate as A;

            if (typedAggregate == null) return;

            var type = typeof(A);

            var properties = type.GetProperties();

            foreach (var property in properties)
            {
                var def = property.GetValue(defaultAggregate, null);
                var val = property.GetValue(untypedAggregate, null);

                if (def != null && val == null)
                {
                    dict.Add(property.Name, null);
                }
                else if (val != null && !val.Equals(def))
                {
                    dict.Add(property.Name, val);
                }
            }
        }

        A defaultAggregate = new A();
    }

    #endregion

    #region The mvc action expression results

    /// <summary>
    /// This class represents an MVC endpoint as defined through an action method
    /// on a controller. Instances of this object correspond roughly to URLs through
    /// the MVC routing system.
    /// SafeMvcUrls creates those objects as intermediaries before converting them
    /// to simple URL strings, as they are often useful themselves.
    /// </summary>
    public class MvcActionExpression
    {
        /// <summary>
        /// The controller name.
        /// </summary>
        public String ControllerName { get; set; }

        /// <summary>
        /// The action name.
        /// </summary>
        public String ActionName { get; set; }

        /// <summary>
        /// The controller descriptor.
        /// </summary>
        public IActionDescriptorCollectionProvider ControllerDescriptor { get; set; }

        /// <summary>
        /// The action descriptor.
        /// </summary>
        public ActionDescriptor ActionDescriptor { get; set; }

        /// <summary>
        /// The route values without the control and action entries.
        /// </summary>
        public RouteValueDictionary Values { get; set; }

        /// <summary>
        /// The protocol, can be null.
        /// </summary>
        public String Protocol { get; set; }

        /// <summary>
        /// The host name, can be null.
        /// </summary>
        public String HostName { get; set; }

        /// <summary>
        /// Performs an implicit conversion from <see cref="ActionResult"/> to <see cref="MvcActionExpression"/>.
        /// </summary>
        /// <param name="ar">The value returned from a .To&lt;C> helper.</param>
        /// <returns>
        /// The action expression.
        /// </returns>
        public static implicit operator MvcActionExpression(ActionResult ar)
        {
            return ar.AsExpression();
        }

        /// <summary>
        /// Performs an implicit conversion from <see cref="Task{ActionResult}"/> to <see cref="MvcActionExpression"/>.
        /// </summary>
        /// <param name="tar">The value returned from a .To&lt;C> helper.</param>
        /// <returns>
        /// The action expression.
        /// </returns>
        public static implicit operator MvcActionExpression(Task<ActionResult> tar)
        {
            return tar.AsExpression();
        }

        /// <summary>
        /// Retrieves the route values with the controller and action entries.
        /// </summary>
        /// <returns>The route values with the controller and action entries.</returns>
        public RouteValueDictionary GetValuesWithControllerAndAction()
        {
            var values = new RouteValueDictionary(Values);
            values["controller"] = ControllerName;
            values["action"] = ActionName;
            return values;
        }

        /// <summary>
        /// Gets a URL corresponding to this expression. This function calls down to `Url.Action(...)`.
        /// </summary>
        /// <param name="url">The `UrlHelper` to use.</param>
        /// <returns>The URL corresponding to the expression.</returns>
        public String GetUrl(IUrlHelper url)
        {
            var values = new RouteValueDictionary(Values);

            foreach (var hook in SafeMvcUrlsHookRegistry.Hooks)
            {
                hook.OnBeforeUrlCreation(url, this, values);
            }

            var urlString = url.Action(ActionName, ControllerName, values, Protocol, HostName);

            foreach (var hook in SafeMvcUrlsHookRegistry.Hooks)
            {
                hook.OnBeforeUrlDelivery(ref urlString);
            }

            return urlString;
        }

        /// <summary>
        /// Determines whether the expression matches the current request in terms of
        /// controller and action.
        /// </summary>
        /// <param name="request">The current request.</param>
        /// <returns>True, if the expression matches the current request.</returns>
        public Boolean IsCurrent(RouteData routeData)
        {
            if (!ControllerName.Equals(routeData.Values["controller"])) return false;
            if (!ActionName.Equals(routeData.Values["action"])) return false;

            return true;
        }
    }

    /// <summary>
    /// The actual results from a call to `.To&lt;C>()` implement `IMvcActionExpressionProvider`. They
    /// also override `Object.ToString()`, which is what gives the URL. A call to `Object.ToString()`
    /// actually calls down to `IMvcActionExpressionProvider.GetExpression().GetUrl(urlHelper)`.
    /// </summary>
    public interface IMvcActionExpressionProvider
    {
        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        MvcActionExpression GetExpression();
    }

    class ActionResultExpressionProvider : ActionResult, IMvcActionExpressionProvider
    {
        public ActionResultExpressionProvider(MvcActionExpression expression, IUrlHelper urlHelper = null)
        {
            this.expression = expression;
            this.urlHelper = urlHelper;
        }

        public MvcActionExpression GetExpression()
        {
            return expression;
        }

        public String ToString(UrlHelper urlHelper)
        {
            return expression.GetUrl(urlHelper);
        }

        public override String ToString()
        {
            if (urlHelper == null) throw new InvalidOperationException("This ActionResult can't create a url without providing an UrlHelper explicitly.");

            return expression.GetUrl(urlHelper);
        }

        public override void ExecuteResult(ActionContext context)
        {
            throw new NotImplementedException("This is a pseudo-result that isn't supposed to be executed.");
        }

        MvcActionExpression expression;

        IUrlHelper urlHelper;
    }

    class ActionResultTaskExpressionProvider : Task<ActionResult>, IMvcActionExpressionProvider
    {
        public ActionResultTaskExpressionProvider(MvcActionExpression expression, IUrlHelper urlHelper = null)
            : base(Impl)
        {
            this.expression = expression;
            this.urlHelper = urlHelper;
        }

        public MvcActionExpression GetExpression()
        {
            return expression;
        }

        public String ToString(UrlHelper urlHelper)
        {
            return expression.GetUrl(urlHelper);
        }

        public override String ToString()
        {
            if (urlHelper == null) throw new InvalidOperationException("This ActionResult can't create a url without providing an UrlHelper explicitly.");

            return expression.GetUrl(urlHelper);
        }

        static ActionResult Impl()
        {
            throw new NotImplementedException("This is a pseudo-action that isn't supposed to be executed.");
        }

        MvcActionExpression expression;

        IUrlHelper urlHelper;
    }

    #endregion

    #region The controller descriptor cache

    class ControllerDescriptorCache
    {
        public String ControllerName { get; set; }
        public Type ControllerType { get; set; }
        public ControllerActionDescriptor[] Actions { get; set; }
        public IActionDescriptorCollectionProvider Provider { get; set; }

        public static ControllerDescriptorCache Get(IServiceProvider services, Type controllerType)
        {
            if (cache == null)
            {
                lock (typeof(ControllerDescriptorCache))
                {
                    cache = Create(services);
                }
            }
            return cache[controllerType];
        }

        static IDictionary<Type, ControllerDescriptorCache> cache = null;

        static IDictionary<Type, ControllerDescriptorCache> Create(IServiceProvider services)
        {
            var provider = services.GetService(typeof(IActionDescriptorCollectionProvider)) as IActionDescriptorCollectionProvider;

            var allActions = provider.ActionDescriptors.Items;

            var result = (
                from a in allActions.OfType<ControllerActionDescriptor>()
                group a by (type: a.ControllerTypeInfo.AsType(), name: a.ControllerName) into g
                select new ControllerDescriptorCache
                {
                    ControllerName = g.Key.name,
                    ControllerType = g.Key.type,
                    Actions = g.ToArray(),
                    Provider = provider
                }
            ).ToDictionary(r => r.ControllerType, r => r);

            return result;
        }
    }

    #endregion

    #region Hooks

    /// <summary>
    /// A hook to various points of the SafeMvcUrls URL creation process.
    /// </summary>
    public abstract class AbstractSafeMvcUrlCreationHook
    {
        /// <summary>
        /// Called before `SafeMvcUrls` populates the route values from the lambda expression. Values
        /// put into the dictionary may get overridden by `SafeMvcUrls`.
        /// </summary>
        /// <param name="values">An empty route values dictionary to populate.</param>
        public virtual void OnBeforeValuesFilled(RouteValueDictionary values) { }

        /// <summary>
        /// Called before a call to `MvcActionExpression.GetUrl(UrlHelper url)` to allow for the addition
        /// of extra values that should only added on the actual string representation of URLs.
        /// </summary>
        /// <param name="url">The used url helper.</param>
        /// <param name="expression">The action expression.</param>
        /// <param name="values">The route values to tweak.</param>
        public virtual void OnBeforeUrlCreation(IUrlHelper url, MvcActionExpression expression, RouteValueDictionary values) { }

        /// <summary>
        /// Called before a call to `MvcActionExpression.GetUrl(UrlHelper url)` returns to deliver the actual URL to
        /// allow for final tweaking.
        /// </summary>
        /// <param name="url">The url to tweak.</param>
        public virtual void OnBeforeUrlDelivery(ref String url) { }
    }

    static class SafeMvcUrlsHookRegistry
    {
        internal static void AddHook(AbstractSafeMvcUrlCreationHook hook)
        {
            hooks.Add(hook);
        }

        internal static IEnumerable<AbstractSafeMvcUrlCreationHook> Hooks { get { return hooks; } }

        static List<AbstractSafeMvcUrlCreationHook> hooks = new List<AbstractSafeMvcUrlCreationHook>();
    }

    #endregion

    #region The IInterceptor implementation doing the main work

    class ActionUrlCreatingInterceptor : IInterceptor
    {
        public ActionUrlCreatingInterceptor(Type controllerType, IUrlHelper urlHelper = null, RouteValueDictionary values = null, String protocol = null, String hostname = null)
        {
            this.controllerType = controllerType;
            this.values = values;
            this.protocol = protocol;
            this.hostname = hostname;
            this.urlHelper = urlHelper;
        }

        public void Intercept(IInvocation invocation)
        {
            var returnType = invocation.Method.ReturnType;

            if (invocation.Method.GetCustomAttribute<NonActionAttribute>() != null)
            {
                throw new Exception(String.Format("You called the controller method {0} that is marked as not being an action on the helper returned by one of the *To() overloads.", invocation.Method));
            }

            if (invocation.Method.GetCustomAttribute<NoSafeMvcUrlsAttribute>() != null)
            {
                throw new Exception(String.Format("You called the controller method {0} that is marked as not being no eligible for safe mvc urls on the helper returned by one of the *To() overloads.", invocation.Method));
            }

            if (!IsAcceptableAsReturnType(returnType))
            {
                throw new Exception(String.Format("You called the controller method {0} that doesn't return an ActionResult on the helper returned by one of the *To() overloads.", invocation.Method));
            }

            var descriptor = ControllerDescriptorCache.Get(
                urlHelper.ActionContext.HttpContext.RequestServices, controllerType
            );

            var actionDescriptor = descriptor.Actions
                .FirstOrDefault(ad => AreMethodsEqualForDeclaringType(ad.MethodInfo, invocation.Method));

            if (actionDescriptor == null) throw new Exception(String.Format("You called the controller method {0} which MVC thinks is not an action on the helper returned by one of the *To() overloads.", invocation.Method));

            // Strange that ActionDescriptor.ActionName doesn't always return the action's name...
            var actionNameAttribute = actionDescriptor.MethodInfo.GetCustomAttribute<ActionNameAttribute>();
            var actionName = actionNameAttribute != null ? actionNameAttribute.Name : actionDescriptor.ActionName;

            // as it is the case with controllers.
            var controllerName = descriptor.ControllerName;

            var values = new RouteValueDictionary();

            foreach (var hook in SafeMvcUrlsHookRegistry.Hooks)
            {
                hook.OnBeforeValuesFilled(values);
            }

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

                if (p.ParameterType.GetTypeInfo().GetCustomAttribute<MvcParameterAggregateAttribute>(true) != null)
                {
                    MvcAggregateHelper.AddValues(values, p.ParameterType, a);
                }
                else
                {
                    if (!p.IsOptional || !AreEqual(p.ParameterType, a, p.DefaultValue))
                    {
                        if (null != a)
                        {
                            values[parameters[i].Name] = Stringify(a);
                        }
                    }
                }
            }

            var expression = new MvcActionExpression()
            {
                ActionName = actionName,
                ControllerName = controllerName,
                ActionDescriptor = actionDescriptor,
                ControllerDescriptor = descriptor.Provider,
                HostName = hostname,
                Protocol = protocol,
                Values = values
            };

            if (returnType.GetTypeInfo().IsGenericType)
            {
                invocation.ReturnValue = new ActionResultTaskExpressionProvider(expression, urlHelper);
            }
            else
            {
                invocation.ReturnValue = new ActionResultExpressionProvider(expression, urlHelper);
            }
        }

        static Boolean AreMethodsEqualForDeclaringType(MethodInfo first, MethodInfo second)
        {
            first = first.ReflectedType == first.DeclaringType ? first : first.DeclaringType.GetMethod(first.Name, first.GetParameters().Select(p => p.ParameterType).ToArray());
            second = second.ReflectedType == second.DeclaringType ? second : second.DeclaringType.GetMethod(second.Name, second.GetParameters().Select(p => p.ParameterType).ToArray());
            return first == second;
        }

        static Object Stringify(Object o)
        {
            return o.ToString();
        }

        internal static Boolean IsAcceptableAsReturnType(Type type)
        {
            if (typeof(IActionResult).IsAssignableFrom(type)) return true;
            if (typeof(Task<IActionResult>).IsAssignableFrom(type)) return true;
            return false;
        }

        static Boolean AreEqual(Type type, Object argument, Object defaultValue)
        {
            if (argument == null) return defaultValue == null;

            // For some bizarre reason, a default of "default(Guid)" leads to *null* as reflected default value.
            // That's not the case with Int32, so I'm a bit at a loss as to what's different with Guid.
            // Anyway, this is the special handling to make Guids behave properly. Non-nullable type's can't be
            // null, so that case is properly identifiable.
            if (type.GetTypeInfo().IsValueType && defaultValue == null) return argument.Equals(Activator.CreateInstance(type));

            return argument.Equals(defaultValue);
        }

        Type controllerType;
        IUrlHelper urlHelper;
        RouteValueDictionary values;
        String protocol;
        String hostname;
    }

    #endregion

    #region The public overloads

    /// <summary>
    /// Extensions for the creation of urls to actions in a name- and type-safe manner.
    /// </summary>
    public static class SafeMvcUrlsExtensions
    {
        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns>
        /// The fake proxy controller.
        /// </returns>
        public static C To<C>(String protocol = null, String hostname = null)
            where C : Controller
        {
            return CreateProxy<C>(typeof(C), null, null, protocol, hostname);
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns>The fake proxy controller.</returns>
        public static C To<C>(this IUrlHelper urlHelper, String protocol = null, String hostname = null)
            where C : Controller
        {
            return CreateProxy<C>(typeof(C), urlHelper, null, protocol, hostname);
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
        public static C To<C>(this IUrlHelper urlHelper, RouteValueDictionary routeValues, String protocol = null, String hostname = null)
            where C : Controller
        {
            return CreateProxy<C>(typeof(C), urlHelper, routeValues, protocol, hostname);
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
        public static C To<C>(this IUrlHelper urlHelper, Object routeValues, String protocol = null, String hostname = null)
            where C : Controller
        {
            var dict = new RouteValueDictionary(routeValues);

            return CreateProxy<C>(typeof(C), urlHelper, dict, protocol, hostname);
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="type">The most derived type of the controller in case it is not know at compile time.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns>
        /// The fake proxy controller.
        /// </returns>
        public static C To<C>(Type type, String protocol = null, String hostname = null)
            where C : Controller
        {
            return CreateProxy<C>(type, null, null, protocol, hostname);
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="type">The most derived type of the controller in case it is not know at compile time.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns>
        /// The fake proxy controller.
        /// </returns>
        public static C To<C>(this IUrlHelper urlHelper, Type type, String protocol = null, String hostname = null)
            where C : Controller
        {
            return CreateProxy<C>(type, urlHelper, null, protocol, hostname);
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="routeValues">Optional route values. Those can be overridden by what is derived from the action call.</param>
        /// <param name="type">The most derived type of the controller in case it is not know at compile time.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns></returns>
        public static C To<C>(this IUrlHelper urlHelper, Type type, RouteValueDictionary routeValues, String protocol = null, String hostname = null)
            where C : Controller
        {
            return CreateProxy<C>(type, urlHelper, routeValues, protocol, hostname);
        }

        /// <summary>
        /// Returns a fake proxy controller the action methods of which, instead of their usual implementation,
        /// return fake objects that return the url to that controller action when having ".ToString()" called on them.
        /// </summary>
        /// <typeparam name="C">The controller type an action of which you want to create a url to.</typeparam>
        /// <param name="urlHelper">Can be retrieved from <c>Url</c> property on both the controller and the view classes.</param>
        /// <param name="type">The most derived type of the controller in case it is not know at compile time.</param>
        /// <param name="routeValues">Optional route values. Those can be overridden by what is derived from the action call.</param>
        /// <param name="protocol">Optionally specify a protocol and this url will be absolute.</param>
        /// <param name="hostname">Optionally specify a hostname and this url will be absolute.</param>
        /// <returns></returns>
        public static C To<C>(this IUrlHelper urlHelper, Type type, Object routeValues, String protocol = null, String hostname = null)
            where C : Controller
        {
            var dict = new RouteValueDictionary(routeValues);

            return CreateProxy<C>(type, urlHelper, dict, protocol, hostname);
        }

        /// <summary>
        /// Converts a set of route values to an object with the route value keys as properties.
        /// </summary>
        /// <param name="values">The route values to use.</param>
        /// <returns>The object representing the route values.</returns>
        public static Object ToObject(this RouteValueDictionary values)
        {
            var obj = new ExpandoObject();
            var dict = obj as IDictionary<String, Object>;

            foreach (var pair in values)
            {
                dict.Add(pair);
            }
            return obj;
        }

        /// <summary>
        /// Gets an `MvcActionExpression` from the the result of a call to `Url.To&lt;C>()`.
        /// </summary>
        /// <param name="result">The `ActionResult` this method extends.</param>
        /// <returns>The `MvcActionExpression` representing the URL.</returns>
        public static MvcActionExpression AsExpression(this ActionResult result)
        {
            var expression = result as IMvcActionExpressionProvider;

            if (expression == null) throw new Exception("This ActionResult is not an mvc action expression result. Are you sure it's been returned from an Url.To<C>() expression?");

            return expression.GetExpression();
        }

        /// <summary>
        /// Gets an `MvcActionExpression` from the the result of a call to `Url.To&lt;C>()`.
        /// </summary>
        /// <param name="result">The `Task&lt;ActionResult>` this method extends.</param>
        /// <returns>The `MvcActionExpression` representing the URL.</returns>
        public static MvcActionExpression AsExpression(this Task<ActionResult> result)
        {
            var expression = result as IMvcActionExpressionProvider;

            if (expression == null) throw new Exception("This Task<ActionResult> is not a mvc action expression result. Are you sure it's been returned from an Url.To<C>() expression?");

            return expression.GetExpression();
        }

        static C CreateProxy<C>(Type type, IUrlHelper urlHelper, RouteValueDictionary routeValues, String protocol, String hostname)
            where C : Controller
        {
            ControllerNanny.Assert(urlHelper.ActionContext.HttpContext.RequestServices, type);

            if (type != typeof(C) && !type.GetTypeInfo().IsSubclassOf(typeof(C)))
                throw new ArgumentException($"The specified type {type.Name} does not derive from {nameof(C)}", nameof(type));

            try
            {
                areInControllerCreation = true;

                // FIXME: we need to reflect on the controller (with the nanny) and check for a ctor that's adequate.
                var args = new object[1];

                var options = ProxyGenerationOptions.Default;

                return generator.CreateClassProxy(type, interfaces, options, args, new ActionUrlCreatingInterceptor(type, urlHelper, routeValues, protocol, hostname)) as C;
            }
            finally
            {
                areInControllerCreation = false;
            }
        }

        [ThreadStatic]
        static Boolean areInControllerCreation = false;

        /// <summary>
        /// Gets a value indicating whether we are currently in the process of constructing
        /// a fake proxy controller. This can be used in the constructors of such controllers
        /// to bail out in such situtations and skip any setup code.
        /// </summary>
        static public Boolean AreInControllerCreation { get { return areInControllerCreation; } }

        static readonly Type[] interfaces = new Type[] { typeof(ISafeMvcUrlsControllerImplementation) };

        static readonly ProxyGenerator generator = new ProxyGenerator();

        /// <summary>
        /// Adds a new hook to tweak SafeMvcUrls' URL creation process.
        /// </summary>
        /// <param name="hook">The hook to add.</param>
        public static void AddHook(AbstractSafeMvcUrlCreationHook hook)
        {
            SafeMvcUrlsHookRegistry.AddHook(hook);
        }
    }

    #endregion
}
