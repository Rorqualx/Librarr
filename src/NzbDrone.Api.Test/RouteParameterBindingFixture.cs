using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;
using Readarr.Api.V1.Books;
using Readarr.Http.Frontend;

namespace NzbDrone.Api.Test
{
    [TestFixture]
    public class RouteParameterBindingFixture
    {
        private static readonly Regex RouteToken = new Regex(@"\{\**(?<name>\w+)", RegexOptions.Compiled);

        // A parameter named in the route template but annotated [FromQuery]
        // or [FromBody] never sees the route value: `restore/{id:int}` with
        // `[FromQuery] int id` binds 0 for /restore/5. The .NET 10 binding
        // sweep did exactly that to five actions.
        [Test]
        public void route_parameters_should_not_be_bound_from_query_or_body()
        {
            var offenders = new List<string>();

            var controllers = new[] { typeof(BookController).Assembly, typeof(StaticResourceController).Assembly }
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t));

            foreach (var controller in controllers)
            {
                var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (var action in actions)
                {
                    var routeTokens = action.GetCustomAttributes<HttpMethodAttribute>(true)
                        .Where(a => a.Template != null)
                        .SelectMany(a => RouteToken.Matches(a.Template).Select(m => m.Groups["name"].Value))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var parameter in action.GetParameters().Where(p => routeTokens.Contains(p.Name)))
                    {
                        if (parameter.GetCustomAttribute<FromQueryAttribute>() != null ||
                            parameter.GetCustomAttribute<FromBodyAttribute>() != null)
                        {
                            offenders.Add($"{controller.Name}.{action.Name}({parameter.Name})");
                        }
                    }
                }
            }

            offenders.Should().BeEmpty();
        }

        // A complex parameter with no binding attribute leaves the source to
        // MVC's inference, and that inference is what bound an empty model
        // to every write action: resource classes are registered with the
        // container, so they were inferred as [FromServices]. Startup now
        // turns that inference off, but the source of a complex parameter
        // should be written down rather than guessed -- this also catches
        // an interface parameter that expects to be injected, which now
        // needs an explicit [FromServices].
        [Test]
        public void complex_action_parameters_should_have_an_explicit_binding_source()
        {
            var offenders = new SortedSet<string>();
            var assemblies = new[] { typeof(BookController).Assembly, typeof(StaticResourceController).Assembly };

            var controllers = assemblies
                .SelectMany(a => a.GetTypes())
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var controller in controllers)
            {
                // Not DeclaredOnly: actions inherited from a generic base
                // only have real parameter types on the closed controller.
                // Otherwise the same cut MVC makes -- every public method
                // of ours is an action unless it says [NonAction], with or
                // without an [Http*] attribute. Framework methods and our
                // overrides of them (OnActionExecuting) are not.
                var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName &&
                                !m.IsGenericMethodDefinition &&
                                assemblies.Contains(m.GetBaseDefinition().DeclaringType.Assembly) &&
                                m.GetCustomAttribute<NonActionAttribute>(true) == null);

                foreach (var action in actions)
                {
                    foreach (var parameter in action.GetParameters().Where(p => IsComplex(p.ParameterType)))
                    {
                        var hasSource = parameter.GetCustomAttributes(true)
                            .Any(a => a is IBindingSourceMetadata || a is IBinderTypeProviderMetadata);

                        if (!hasSource)
                        {
                            offenders.Add($"{controller.Name}.{action.Name}({parameter.ParameterType.Name} {parameter.Name})");
                        }
                    }
                }
            }

            offenders.Should().BeEmpty();
        }

        // The same test MVC applies: a type is simple when it can be
        // converted from a string. Collections of simple types count as
        // complex, which is right -- they are inferred as body too.
        private static bool IsComplex(Type type)
        {
            if (type == typeof(CancellationToken))
            {
                return false;
            }

            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            return !TypeDescriptor.GetConverter(underlying).CanConvertFrom(typeof(string));
        }
    }
}
