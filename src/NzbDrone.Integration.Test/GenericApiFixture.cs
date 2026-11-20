using System.Linq;
using System.Net;
using FluentAssertions;
using NUnit.Framework;
using RestSharp;

namespace NzbDrone.Integration.Test
{
    [TestFixture]
    public class GenericApiFixture : IntegrationTest
    {
        [TestCase("application/json")]
        [TestCase("text/html, application/json")]
        [TestCase("application/xml, application/json")]
        [TestCase("text/html, */*")]
        [TestCase("*/*")]
        [TestCase("")]
        public void should_get_json_with_accept_header(string header)
        {
            var request = new RestRequest("system/status")
            {
                RequestFormat = DataFormat.None
            };
            request.AddHeader("Accept", header);

            var response = RestClient.Execute(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            // RestSharp 107+ parses ContentType down to the media type alone,
            // so it reads "application/json" and the charset is gone. 106
            // handed back the raw header. Assert against the header itself to
            // keep testing what the server actually sent, charset included.
            ((string)response.ContentHeaders.Single(h => h.Name == "Content-Type").Value)
                .Should().Be("application/json; charset=utf-8");
        }

        [TestCase("application/xml")]
        [TestCase("text/html")]
        [TestCase("application/junk")]
        public void should_get_unacceptable_with_accept_header(string header)
        {
            var request = new RestRequest("system/status")
            {
                RequestFormat = DataFormat.None
            };
            request.AddHeader("Accept", header);

            var response = RestClient.Execute(request);

            response.StatusCode.Should().Be(HttpStatusCode.NotAcceptable);
        }
    }
}
