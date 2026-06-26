using AgentCommon;
using Xunit;

namespace D2RAgent.Tests;

public sealed class LogFileRotatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "d2r-filelogger-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void FirstRunReturnsLogZeroWithoutCreatingAnyFiles()
    {
        var path = LogFileRotator.RotateAndPrepare(_dir);

        Assert.Equal(Path.Combine(_dir, "log.0"), path);
        Assert.True(Directory.Exists(_dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void EachRunShiftsPriorLogsDownByOne()
    {
        WriteRun("run1");
        WriteRun("run2");

        Assert.Equal("run2", File.ReadAllText(Path.Combine(_dir, "log.0")));
        Assert.Equal("run1", File.ReadAllText(Path.Combine(_dir, "log.1")));
        Assert.False(File.Exists(Path.Combine(_dir, "log.2")));
    }

    [Fact]
    public void OldestRunIsDroppedBeyondKeepLimit()
    {
        WriteRun("run1");
        WriteRun("run2");
        WriteRun("run3");
        WriteRun("run4");

        Assert.Equal("run4", File.ReadAllText(Path.Combine(_dir, "log.0")));
        Assert.Equal("run3", File.ReadAllText(Path.Combine(_dir, "log.1")));
        Assert.Equal("run2", File.ReadAllText(Path.Combine(_dir, "log.2")));
        Assert.False(File.Exists(Path.Combine(_dir, "log.3")));
        Assert.Equal(3, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void RespectsCustomKeepCount()
    {
        WriteRun("run1", keep: 2);
        WriteRun("run2", keep: 2);
        WriteRun("run3", keep: 2);

        Assert.Equal("run3", File.ReadAllText(Path.Combine(_dir, "log.0")));
        Assert.Equal("run2", File.ReadAllText(Path.Combine(_dir, "log.1")));
        Assert.False(File.Exists(Path.Combine(_dir, "log.2")));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    private void WriteRun(string content, int keep = 3)
    {
        var path = LogFileRotator.RotateAndPrepare(_dir, keep);
        File.WriteAllText(path, content);
    }
}
