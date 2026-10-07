using System.Reflection;
using Xunit;

namespace GGScale.Tests
{
    public class SdkVersionTests
    {
        [Fact]
        public void Value_is_a_semantic_version()
        {
            Assert.Matches(@"^\d+\.\d+\.\d+", SdkVersion.Value);
        }

        [Fact]
        public void Value_matches_the_package_version()
        {
            var version = typeof(SdkVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

            Assert.Equal(SdkVersion.Value, version.Split('+')[0]);
        }
    }
}
