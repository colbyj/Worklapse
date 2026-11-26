using Worklapse.Video;

namespace Worklapse.Tests;

public class NativeHeapTests
{
    [Fact]
    public void TrimmingWorksOnEveryPlatform()
    {
        // On Linux this calls glibc's malloc_trim; a wrong binding would throw here, not in the app.
        NativeHeap.Trim();
        NativeHeap.Trim();
    }
}
