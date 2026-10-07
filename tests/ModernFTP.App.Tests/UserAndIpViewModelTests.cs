using ModernFTP.Config;
using ModernFTP.Engine;

namespace ModernFTP.App.Tests;

public class UserAndIpViewModelTests
{
    [Fact]
    public void SetPasswordStoresOnlyAPbkdf2Hash()
    {
        var user = new UserConfig { Username = "a", Password = "plain" };
        Assert.Null(UserSetupViewModel.SetPassword(user, "s3cret"));
        Assert.Null(user.Password);
        var credential = new Pbkdf2Credential(Convert.FromBase64String(user.PasswordHash!), Convert.FromBase64String(user.PasswordSalt!), user.PasswordIterations);
        Assert.True(credential.Verify("s3cret"));
        Assert.False(credential.Verify("plain"));
    }

    [Fact]
    public void UserListActionsKeepOtherSettings()
    {
        var config = new ModernFtpConfig { LoginTimeoutSeconds = 55 };
        var model = new UserSetupViewModel(config);
        Assert.Null(model.NewUser("alice"));
        Assert.NotNull(model.NewUser("ALICE"));
        UserSetupViewModel.AddDirectory(model.Find("alice")!, @"C:\FTP", null, true);
        Assert.Null(model.CopyUser("alice", "bob"));
        Assert.Single(model.Find("bob")!.Directories);
        Assert.Null(model.RenameUser("bob", "carol"));
        model.DeleteUser("alice");
        Assert.Equal(["carol"], model.UserNames);
        Assert.Equal(55, config.LoginTimeoutSeconds);
    }

    [Fact]
    public void IpListRejectsBadAndDuplicateEntries()
    {
        var config = new ModernFtpConfig { LoginTimeoutSeconds = 55, BannedAddresses = ["10.0.0.1"] };
        var model = new IpRestrictionViewModel(config);
        Assert.Null(model.Add("192.0.2.0/24"));
        Assert.NotNull(model.Add("not an ip"));
        Assert.NotNull(model.Add("10.0.0.1"));
        model.Remove("10.0.0.1");
        var path = Path.Combine(Path.GetTempPath(), "modernftp-ip-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Assert.Null(model.Save(path));
            var saved = ConfigLoader.Load(path);
            Assert.Equal(["192.0.2.0/24"], saved.BannedAddresses);
            Assert.Equal(55, saved.LoginTimeoutSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
