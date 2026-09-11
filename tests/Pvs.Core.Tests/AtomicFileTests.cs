using Pvs.Core.Persistence;
using Xunit;

namespace Pvs.Core.Tests;

public class AtomicFileTests
{
    [Fact]
    public void Write_keeps_the_previous_file_as_backup_and_Load_falls_back_to_it_when_the_main_is_corrupt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pvs-atomic-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "state.json");
        AtomicFile.Write(path, "{\"n\":1}");
        AtomicFile.Write(path, "{\"n\":2}");
        Assert.Equal("{\"n\":1}", File.ReadAllText(path + ".bak"));
        Assert.Equal(2, AtomicFile.Load(path, t => (int?)System.Text.Json.JsonDocument.Parse(t).RootElement.GetProperty("n").GetInt32()));
        File.WriteAllText(path, "{\"n\":");   // a power cut mid-write
        Assert.Equal(1, AtomicFile.Load(path, t => (int?)System.Text.Json.JsonDocument.Parse(t).RootElement.GetProperty("n").GetInt32()));
        AtomicFile.Delete(path);
        Assert.Null(AtomicFile.Load(path, t => (int?)1));
        Directory.Delete(dir, true);
    }
}
