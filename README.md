SafeMvcUrls
===========

Type- and name-safe urls with ASP.NET MVC.

# What is the syntax?

For a controller action defined as

```cs
public class MyController : Controller
{
  public virtual ActionResult MyAction(String myString = "default", Int32 myInt = -1) { return View(); }
}
```

you can create urls in views with

```
<a href="@(Url.To<MyController>().MyAction("foo", 42))">link</a>
```

or in controllers with

```cs
return Redirect(Url.To<MyController>().MyAction("foo", 42).ToString());
```

and get what you expect as the url:

```
/MyController/MyAction?myString=foo&myInt=42
```

# How do I get started?

- Install the [NuGet package](https://www.nuget.org/packages/IronStone.Web.Mvc.SafeUrls).
- Add the `IronStone.Web.Mvc` namespace to your Web.Config for your views along with those you already have. You can alternatively use a `using` statement in your view. It will then look something like this:
```
  <system.web.webPages.razor>
    <pages pageBaseType="System.Web.Mvc.WebViewPage">
      <namespaces>
        <add namespace="System.Web.Mvc" />
        <add namespace="System.Web.Mvc.Ajax" />
        <add namespace="System.Web.Mvc.Html" />
        <add namespace="System.Web.Routing" />
        <add namespace="IronStone.Web.Mvc"/>
        ...
```
- Add the `IronStone.Web.Mvc.SafeUrls` assembly to your Web.Config in the `compilation` section. It will then look something like this:
```
  <system.web>
    <compilation debug="true" targetFramework="4.5">
      <assemblies>
        <add assembly="IronStone.Web.Mvc.SafeUrls" />
      </assemblies>
    </compilation>
    <httpRuntime targetFramework="4.5" />
  </system.web>
```
  I always have to restart Visual Studio before Intellisense starts working in my views after doing this.
- Add a `using IronStone.Web.Mvc` declaration to all controller source files in which you want to have the overload set.
- You may have to slightly change the action signatures, see the next section for details.
- Have fun.

# What else should I know?

The helper works by subclassing and overriding your controllers. That requires your controllers to allow that: They mustn't be final, all actions must be virtual and all return values must be one of the two that SafeMvcUrls can deal with: Either `ActionResult` or `Task<ActionResult>`.

If you use the `.To<C>()` extension method on a controller that doesn't match these requirements, in most cases you will get an exception at this point for your own safety: We don't want that you actually call your actions where you merely wanted an url.

For more details just look at the tests at the end of [`SafeMvcUrls.cs`](https://github.com/jtheisen/safe-mvc-urls/blob/master/SafeMvcUrls/SafeMvcUrls.cs).

By the way: This file is the whole implementation; which means that instead of using the NuGet package you can also just include the file in your project. Just make sure that [Castle.Core](http://www.nuget.org/packages/Castle.Core) is also there.

# How does it compare to T4MVC?

Both packages address the same problem, the difference is mostly in the implementation, and to a lesser degree in the syntax and semantics.

## Differences in Implementation

T4MVC uses T4 code generation, SafeMvcUrls uses another well-know heavy gun designed to tackle problems of code generation: [Dynamic Proxy](http://www.castleproject.org/projects/dynamicproxy/).

Depending on how you look at it, that makes SafeMvcUrls more or less light-weight: Both T4MVC and SafeMvcUrls create fake proxy controllers that create urls from their dummy implementations, but SafeMvcUrls doesn't create source files during the build process. Rather, it uses Dynamic Proxy to create those classes at runtime.

This is why, on installing T4MVC, Visual Studio asks you whether you trust the package while on the other hand SafeMvcUrls depends on [Castle.Core](http://www.nuget.org/packages/Castle.Core), the home of Dynamic Proxy.

## Differences in Syntax

Besides the obvious difference of having an overload on the `UrlHelper`, SafeMvcUrls really only provides one set of overloads, `To<Controller>(...)`. Nothing else. In particular, you don't get any overloads on `Controller` or `HtmlHelper` and it doesn't concern itself with anything but the creation of urls. Html is not its business.

## Differences in Semantics

SafeMvcUrls does not normally create gratuitous query arguments:

```
Url.To<MyController>().MyAction(myInt: -1)
```

gives merely

```
/MyController/MyAction
```

because `-1` was the default anyway.
