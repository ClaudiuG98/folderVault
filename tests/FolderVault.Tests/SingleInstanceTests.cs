using System.Security.Cryptography;
using System.Text;
using FolderVault.App;
using Xunit;

namespace FolderVault.Tests;

/// <summary>
/// Guards the single-instance naming scheme.
///
/// This exists because of a real bug: the name was originally built from
/// <c>Environment.UserName.GetHashCode()</c>. .NET randomises string hashing per process, so
/// every launch derived a different mutex name, every process concluded it was the primary, and
/// each double-click on a locked folder started another copy of the app - each with its own
/// unlocked keys and auto-lock timers. Ten processes were found running at once.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void SuffixIsAStableHash_NotAProcessRandomisedOne()
    {
        const string user = "some-user";

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];

        Assert.Equal(expected, SingleInstance.StableSuffix(user));
    }

    [Fact]
    public void SuffixDiffersPerUser_SoTwoSignedInUsersDoNotShareAnInstance()
    {
        Assert.NotEqual(SingleInstance.StableSuffix("alice"), SingleInstance.StableSuffix("bob"));
    }

    [Fact]
    public void SuffixIsRepeatable()
    {
        Assert.Equal(SingleInstance.StableSuffix("claud"), SingleInstance.StableSuffix("claud"));
    }

    /// <summary>
    /// A named mutex cannot contain a backslash outside its namespace prefix, and must be short
    /// enough to be a valid kernel object name.
    /// </summary>
    [Fact]
    public void SuffixIsSafeInsideAKernelObjectName()
    {
        var suffix = SingleInstance.StableSuffix(Environment.UserName);

        Assert.Equal(16, suffix.Length);
        Assert.Matches("^[0-9A-F]{16}$", suffix);
    }
}
