using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
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
    }
}
