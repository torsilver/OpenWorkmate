using OpenWorkmate.Server;
using Xunit;

namespace backend.Tests.Unit;

public sealed class DebugLogHelperRetentionTests
{
    [Fact]
    public void DeleteRollingLogsOlderThanDays_RemovesOnlyExpiredWhitelistFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "owm-log-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var old = Path.Combine(dir, "openworkmate-20200101.txt");
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));

            var fresh = Path.Combine(dir, "openworkmate-20260116.txt");
            File.WriteAllText(fresh, "y");
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddHours(-1));

            var notWhitelisted = Path.Combine(dir, "openworkmate-evil.txt");
            File.WriteAllText(notWhitelisted, "z");
            File.SetLastWriteTimeUtc(notWhitelisted, DateTime.UtcNow.AddDays(-10));

            var other = Path.Combine(dir, "notes.txt");
            File.WriteAllText(other, "w");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-10));

            var n = DebugLogHelper.DeleteRollingLogsOlderThanDays(5, dir);
            Assert.Equal(1, n);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(notWhitelisted));
            Assert.True(File.Exists(other));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }

    [Fact]
    public void DeleteRollingLogsOlderThanDays_Returns0_WhenRetentionInvalid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "owm-log-retention2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "openworkmate-20200101.txt");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
            Assert.Equal(0, DebugLogHelper.DeleteRollingLogsOlderThanDays(0, dir));
            Assert.True(File.Exists(path));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
