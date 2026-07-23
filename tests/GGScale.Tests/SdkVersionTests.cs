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
    }
}
