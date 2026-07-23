using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;

namespace GGScale.IntegrationTests
{
    /// <summary>
    /// Smoke check that the docker compose stack is up. Run via
    /// `make test-integration`, which starts the stack, seeds it, and
    /// sets GGSCALE_IT_BASE_URL.
    /// </summary>
    public class HealthzTests
    {
        internal static string BaseUrl =>
            ItFixture.BaseUrl;

        [Fact]
        public async Task Healthz_responds_ok()
        {
            using var http = new HttpClient();
            using var resp = await http.GetAsync(new Uri(BaseUrl + "/v1/healthz"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
