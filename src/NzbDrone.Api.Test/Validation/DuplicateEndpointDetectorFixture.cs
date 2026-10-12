using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Readarr.Http.Validation;

namespace NzbDrone.Api.Test.Validation
{
    // The detector reflects over routing internals (DfaMatcherBuilder and
    // DfaNode), so these run it against the real routing services: if a
    // runtime upgrade changes the shape of either, this is where it shows.
    [TestFixture]
    public class DuplicateEndpointDetectorFixture
    {
        private DuplicateEndpointDetector _subject;

        [SetUp]
        public void Setup()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddRouting();

            _subject = new DuplicateEndpointDetector(services.BuildServiceProvider());
        }

        private static RouteEndpoint GivenEndpoint(string template, string displayName)
        {
            return new RouteEndpoint(context => context.Response.CompleteAsync(),
                RoutePatternFactory.Parse(template),
                0,
                new EndpointMetadataCollection(new HttpMethodMetadata(new[] { "GET" })),
                displayName);
        }

        [Test]
        public void should_report_two_endpoints_on_the_same_route()
        {
            var dataSource = new DefaultEndpointDataSource(
                GivenEndpoint("api/v1/thing", "First"),
                GivenEndpoint("api/v1/thing", "Second"),
                GivenEndpoint("api/v1/other", "Third"));

            var duplicates = _subject.GetDuplicateEndpoints(dataSource);

            duplicates.Should().HaveCount(1);
            duplicates.Single().Key.Should().Contain("/api/v1/thing/");
            duplicates.Single().Value.Should().BeEquivalentTo(new List<string> { "First", "Second" });
        }

        [Test]
        public void should_report_nothing_when_every_route_is_distinct()
        {
            var dataSource = new DefaultEndpointDataSource(
                GivenEndpoint("api/v1/thing", "First"),
                GivenEndpoint("api/v1/thing/{id:int}", "Second"),
                GivenEndpoint("api/v1/other", "Third"));

            _subject.GetDuplicateEndpoints(dataSource).Should().BeEmpty();
        }

        // Not a real conflict, but the detector reports it: candidates are
        // grouped before route constraints run, so a literal segment and a
        // constrained optional parameter land on the same node. Every entry
        // the live endpoint lists today is of this kind.
        [Test]
        public void should_report_a_literal_route_alongside_a_constrained_optional_parameter()
        {
            var dataSource = new DefaultEndpointDataSource(
                GivenEndpoint("api/v1/thing/{id:int?}", "ById"),
                GivenEndpoint("api/v1/thing/editor", "Editor"));

            var duplicates = _subject.GetDuplicateEndpoints(dataSource);

            duplicates.Should().HaveCount(1);
            duplicates.Single().Value.Should().BeEquivalentTo(new List<string> { "ById", "Editor" });
        }
    }
}
