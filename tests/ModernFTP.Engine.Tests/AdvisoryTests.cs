using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModernFTP.Config;

namespace ModernFTP.Engine.Tests;

/// <summary>Tests for the advisory findings of the P0 and P1 reviews.</summary>
public class AdvisoryTests
{
    // ---- P0 A7: REST applies to the next RETR, STOR or APPE only ------------------------------

    [Fact]
    public async Task RestOffsetIsClearedByAnyOtherCommand()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.All);
        var target = Path.Combine(server.Home, "big.txt");
        await File.WriteAllTextAsync(target, "0123456789");
        await using var client = await server.ConnectAsync();
        Assert.Equal(350, (await client.SendAsync("REST 3")).Code);
        Assert.Equal(257, (await client.SendAsync("PWD")).Code);
        Assert.Equal(250, (await client.SendAsync("CWD /")).Code);
        using (var data = await client.OpenPassiveAsync())
        {
            Assert.Equal(150, (await client.SendAsync("STOR big.txt")).Code);
            await data.GetStream().WriteAsync("abc"u8.ToArray());
        }

        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
        Assert.Equal("abc", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RestOffsetStillAppliesToTheImmediatelyFollowingRetr()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.All);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "r.txt"), "0123456789");
        await using var client = await server.ConnectAsync();
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(350, (await client.SendAsync("REST 4")).Code);
        Assert.Equal(150, (await client.SendAsync("RETR r.txt")).Code);
        using var reader = new StreamReader(data.GetStream());
        Assert.Equal("456789", await reader.ReadToEndAsync());
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
    }

    [Fact]
    public async Task RestBeforePasvStillAppliesToTheRetr()
    {
        await using var server = await TestServer.StartAsync(FtpPermissions.All);
        await File.WriteAllTextAsync(Path.Combine(server.Home, "r.txt"), "0123456789");
        await using var client = await server.ConnectAsync();
        Assert.Equal(350, (await client.SendAsync("REST 4")).Code);
        Assert.Equal(200, (await client.SendAsync("TYPE I")).Code);
        using var data = await client.OpenPassiveAsync();
        Assert.Equal(150, (await client.SendAsync("RETR r.txt")).Code);
        using var reader = new StreamReader(data.GetStream());
        Assert.Equal("456789", await reader.ReadToEndAsync());
        Assert.Equal(226, (await client.ReadReplyAsync()).Code);
    }

    // ---- P0 A8: PASS timing and failed login throttle -----------------------------------------

    [Fact]
    public async Task UnknownAndDisabledUsersCostTheSamePbkdf2WorkAsAWrongPassword()
    {
        await using var server = await TestServer.StartAsync(configure: o =>
        {
            var heavy = Pbkdf2Credential.Create("right", iterations: 300_000);
            o.LoginFailureLimit = 0;
            o.MaxFailedLogins = 0;
            o.Users =
            [
                new FtpUser { UserName = "real", Credential = heavy, HomeDirectory = Path.GetTempPath(), Permissions = new PermissionRules(FtpPermissions.All) },
                new FtpUser { UserName = "off", Enabled = false, Credential = heavy, HomeDirectory = Path.GetTempPath(), Permissions = new PermissionRules(FtpPermissions.All) },
            ];
        });
        async Task<double> TimeAsync(string user)
        {
            await using var client = await server.ConnectAsync(login: false);
            Assert.Equal(331, (await client.SendAsync($"USER {user}")).Code);
            var watch = Stopwatch.StartNew();
            Assert.Equal(530, (await client.SendAsync("PASS wrong")).Code);
            return watch.Elapsed.TotalMilliseconds;
        }

        await TimeAsync("real");
        var real = Math.Min(await TimeAsync("real"), await TimeAsync("real"));
        var unknown = Math.Min(await TimeAsync("nobody"), await TimeAsync("nobody"));
        var disabled = Math.Min(await TimeAsync("off"), await TimeAsync("off"));
        Assert.True(unknown > real * 0.5, $"real {real:0} ms, unknown {unknown:0} ms");
        Assert.True(disabled > real * 0.5, $"real {real:0} ms, disabled {disabled:0} ms");
    }

    [Fact]
    public async Task FailedLoginsFromOneAddressAreDelayedThenBanned()
    {
        await using var server = await TestServer.StartAsync(configure: o =>
        {
            o.LoginFailureLimit = 2;
            o.LoginThrottleDelay = TimeSpan.FromMilliseconds(600);
            o.LoginBanDuration = TimeSpan.FromSeconds(2);
            o.MaxFailedLogins = 0;
        });
        await using var client = await server.ConnectAsync(login: false);
        async Task<(int Code, double Ms)> PassAsync(string password)
        {
            Assert.Equal(331, (await client.SendAsync($"USER {TestServer.UserName}")).Code);
            var watch = Stopwatch.StartNew();
            var reply = await client.SendAsync($"PASS {password}");
            return (reply.Code, watch.Elapsed.TotalMilliseconds);
        }

        Assert.True((await PassAsync("bad")).Ms < 500);
        Assert.True((await PassAsync("bad")).Ms < 500);

        // Two failures reached the limit: every further PASS waits, whether or not the password is right.
        var delayed = await PassAsync("bad");
        Assert.Equal(530, delayed.Code);
        Assert.True(delayed.Ms >= 550, $"{delayed.Ms:0} ms");

        // Four failures (twice the limit) ban the address.
        Assert.Equal(530, (await PassAsync("bad")).Code);
        var banned = await PassAsync(TestServer.Password);
        Assert.Equal(421, banned.Code);

        await using var refused = await FtpTestClient.ConnectAsync(server.Port);
        Assert.Equal(421, (await refused.ReadReplyAsync()).Code);

        // The ban is temporary.
        await Task.Delay(2200);
        await using var again = await server.ConnectAsync();
        Assert.Equal(200, (await again.SendAsync("NOOP")).Code);
    }

    [Fact]
    public void LoginThrottleDefaultsAndConfigReachTheEngine()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            var options = ConfigLoader.ToServerOptions(ConfigLoader.Parse("""{ "tls": { "enabled": false } }"""), directory);
            Assert.Equal(5, options.LoginFailureLimit);
            Assert.Equal(TimeSpan.FromMinutes(10), options.LoginFailureWindow);
            Assert.Equal(TimeSpan.FromMinutes(15), options.LoginBanDuration);
            Assert.Equal(TimeSpan.FromSeconds(2), options.LoginThrottleDelay);

            options = ConfigLoader.ToServerOptions(
                ConfigLoader.Parse("""{ "loginFailureLimit": 3, "loginFailureWindowMinutes": 4, "loginBanMinutes": 7, "tls": { "enabled": false } }"""),
                directory);
            Assert.Equal(3, options.LoginFailureLimit);
            Assert.Equal(TimeSpan.FromMinutes(4), options.LoginFailureWindow);
            Assert.Equal(TimeSpan.FromMinutes(7), options.LoginBanDuration);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- P0 A9: weak hash parameters ----------------------------------------------------------

    [Theory]
    [InlineData(16, 16, 600_000, "passwordHash must be at least 32 bytes")]
    [InlineData(32, 8, 600_000, "passwordSalt at least 16 bytes")]
    [InlineData(32, 16, 99_999, "passwordIterations at least 100,000")]
    public void WeakPasswordParametersAreRejected(int hashBytes, int saltBytes, int iterations, string expected)
    {
        var config = ConfigWithUser(hashBytes, saltBytes, iterations, out var directory);
        try
        {
            var error = Assert.Single(ConfigLoader.Validate(config, directory));
            Assert.Contains(expected.Replace(",", ",", StringComparison.Ordinal), error.Replace("100,000", "100,000", StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValuesFromHashPasswordPassValidation()
    {
        var credential = Pbkdf2Credential.Create("pw");
        var config = ConfigWithUser(credential.Hash.Length, credential.Salt.Length, credential.Iterations, out var directory);
        try
        {
            config.Users[0].PasswordHash = Convert.ToBase64String(credential.Hash);
            config.Users[0].PasswordSalt = Convert.ToBase64String(credential.Salt);
            Assert.Empty(ConfigLoader.Validate(config, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModernFtpConfig ConfigWithUser(int hashBytes, int saltBytes, int iterations, out string directory)
    {
        directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        Directory.CreateDirectory(Path.Combine(directory, "home"));
        return new ModernFtpConfig
        {
            Tls = { Enabled = false },
            Users =
            [
                new UserConfig
                {
                    Username = "u",
                    HomeDirectory = "home",
                    PasswordHash = Convert.ToBase64String(new byte[hashBytes].Select(_ => (byte)7).ToArray()),
                    PasswordSalt = Convert.ToBase64String(new byte[saltBytes].Select(_ => (byte)9).ToArray()),
                    PasswordIterations = iterations,
                },
            ],
        };
    }

    // ---- P0 A12: failed control TLS handshake -------------------------------------------------

    [Fact]
    public async Task FailedControlHandshakeIsReportedAsATlsFailure()
    {
        var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(CertificateProvider.CreateSelfSignedPfx(), password: null);
        await using var server = await TestServer.StartAsync(configure: o => o.Certificate = certificate);
        var reasons = new List<string>();
        using var subscription = server.Server.Subscribe(e =>
        {
            if (e is DisconnectedEvent d)
            {
                lock (reasons)
                {
                    reasons.Add(d.Reason);
                }
            }
        });
        await using (var client = await server.ConnectAsync(login: false))
        {
            Assert.Equal(234, (await client.SendAsync("AUTH TLS")).Code);
            await client.WriteAsync("this is not a TLS client hello\r\n");
            Assert.Null(await client.ReadLineOrNullAsync(TimeSpan.FromSeconds(5)));
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            lock (reasons)
            {
                if (reasons.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        lock (reasons)
        {
            Assert.Equal(["TLS negotiation failed"], reasons);
        }
    }

    // ---- P0 A13: slow throttled transfers count as activity -----------------------------------

    [Fact]
    public async Task ThrottledStreamReportsActivityForEveryChunk()
    {
        var chunks = 0;
        await using var sink = new MemoryStream();
        await using var throttled = new ThrottledStream(sink, 100_000, () => chunks++);
        await throttled.WriteAsync(new byte[30_000]);
        Assert.Equal(3, chunks);
        Assert.Equal(30_000, sink.Length);
    }

    // ---- P1 A6: operator disconnect never waits on a client that stopped reading --------------

    [Fact]
    public async Task DisconnectOfAClientThatStoppedReadingTakesAboutASecond()
    {
        await using var server = await TestServer.StartAsync();
        using var tcp = new TcpClient { ReceiveBufferSize = 1024 };
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        var feat = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("FEAT\r\n", 200)));
        using var stopWriting = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            try
            {
                while (!stopWriting.IsCancellationRequested)
                {
                    await stream.WriteAsync(feat, stopWriting.Token);
                }
            }
            catch (Exception)
            {
            }
        });
        await Task.Delay(2000);

        var id = server.Server.Sessions.Single().Id;
        var watch = Stopwatch.StartNew();
        Assert.True(server.Server.Disconnect(id));
        while (server.Server.Sessions.Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(20);
        }

        await stopWriting.CancelAsync();
        Assert.Empty(server.Server.Sessions);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Disconnect took {watch.Elapsed}");
        await writer;
    }

    // ---- P1 A8: saves keep unknown keys, mode and write atomically ----------------------------

    [Fact]
    public void SavingKeepsUnknownKeysAndTheOriginalFileMode()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "config.json");
            File.WriteAllText(path, """
                {
                  // a comment (cannot be kept)
                  "port": 2121,
                  "futureSetting": { "a": 1 },
                  "users": [
                    { "username": "ann", "note": "keep me", "homeDirectory": "h1" },
                    { "username": "bob", "homeDirectory": "h2" }
                  ]
                }
                """);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var config = ConfigLoader.Load(path);
            config.Port = 2222;
            config.Users.RemoveAt(1);
            config.Users[0].HomeDirectory = "h3";
            ConfigLoader.Save(config, path);

            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(2222, (int)saved["port"]!);
            Assert.Equal(1, (int)saved["futureSetting"]!["a"]!);
            var users = saved["users"]!.AsArray();
            var ann = Assert.Single(users)!;
            Assert.Equal("keep me", (string)ann["note"]!);
            Assert.Equal("h3", (string)ann["homeDirectory"]!);
            Assert.False(File.Exists(path + ".tmp"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }

            // ConfigStore writes the same way.
            new ConfigStore(path).SaveBannedAddresses(["10.0.0.1"]);
            saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(1, (int)saved["futureSetting"]!["a"]!);
            Assert.Equal("10.0.0.1", (string)saved["bannedAddresses"]![0]!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SavingANewFileWorks()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            var path = Path.Combine(directory, "sub", "config.json");
            ConfigLoader.Save(new ModernFtpConfig { Port = 2323 }, path);
            Assert.Equal(2323, ConfigLoader.Load(path).Port);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- P1 A9: a missing home is a warning ----------------------------------------------------

    [Fact]
    public async Task MissingHomeIsAWarningAtStartAndRefusesThatUsersLogin()
    {
        var root = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            var missing = Path.Combine(root, "missing");
            var config = ConfigLoader.Parse("""{ "tls": { "enabled": false } }""");
            config.Users =
            [
                PlainUser("here", missing, enabled: true),
                PlainUser("gone", missing, enabled: false),
            ];
            config.AllowPlaintextPasswords = true;
            Assert.Empty(ConfigLoader.Validate(config, root));

            var options = ConfigLoader.ToServerOptions(config, root);
            options.ListenAddress = IPAddress.Loopback;
            options.Port = 0;
            options.FailedLoginDelay = TimeSpan.Zero;
            var messages = new List<string>();
            await using var server = new FtpServer(options);
            using var subscription = server.Subscribe(e =>
            {
                if (e is ErrorEvent error)
                {
                    lock (messages)
                    {
                        messages.Add(error.Message);
                    }
                }
            });
            await server.StartAsync();

            await using var client = await FtpTestClient.ConnectAsync(server.LocalEndPoint!.Port);
            Assert.Equal(220, (await client.ReadReplyAsync()).Code);
            Assert.Equal(331, (await client.SendAsync("USER here")).Code);
            Assert.Equal(530, (await client.SendAsync("PASS pw")).Code);

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                lock (messages)
                {
                    if (messages.Count(m => m.Contains("warning", StringComparison.Ordinal)) > 0)
                    {
                        break;
                    }
                }

                await Task.Delay(20);
            }

            lock (messages)
            {
                var warning = Assert.Single(messages, m => m.StartsWith("warning:", StringComparison.Ordinal));
                Assert.Contains("'here'", warning, StringComparison.Ordinal);
                Assert.Contains(missing, warning, StringComparison.Ordinal);
                Assert.DoesNotContain(messages, m => m.Contains("'gone'", StringComparison.Ordinal));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static UserConfig PlainUser(string name, string home, bool enabled) =>
        new() { Username = name, Password = "pw", HomeDirectory = home, Enabled = enabled };

    // ---- P0 A14 and A16: config rules ---------------------------------------------------------

    [Fact]
    public void AnUnreadablePfxIsAnInvalidConfigNotACrashAtStart()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "bad.pfx"), "x");
            var config = new ModernFtpConfig { Tls = { Enabled = true, CertificatePath = "bad.pfx" } };
            Assert.Contains(ConfigLoader.Validate(config, directory), e => e.Contains("tls.certificatePath is not a usable PFX", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PlaintextPasswordsNeedTheDevelopmentFlag()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "home"));
            var config = new ModernFtpConfig { Tls = { Enabled = false }, Users = [PlainUser("u", "home", enabled: true)] };
            Assert.Contains(ConfigLoader.Validate(config, directory), e => e.Contains("allowPlaintextPasswords", StringComparison.Ordinal));
            config.AllowPlaintextPasswords = true;
            Assert.Empty(ConfigLoader.Validate(config, directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnonymousEntryIsSkippedUnlessAllowed()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "home"));
            var config = new ModernFtpConfig { Tls = { Enabled = false }, Users = [new UserConfig { Username = "anonymous", HomeDirectory = "home" }] };
            Assert.Empty(ConfigLoader.ToServerOptions(config, directory).Users);
            config.AllowAnonymous = true;
            var user = Assert.Single(ConfigLoader.ToServerOptions(config, directory).Users);
            Assert.True(user.Credential.Verify("anything"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RelativeHomesResolveAgainstTheConfigFolder()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "ftp", "home"));
            var config = new ModernFtpConfig { Tls = { Enabled = false }, AllowPlaintextPasswords = true, Users = [PlainUser("u", Path.Combine("ftp", "home"), enabled: true)] };
            var user = Assert.Single(ConfigLoader.ToServerOptions(config, directory).Users);
            Assert.Equal(Path.Combine(directory, "ftp", "home"), user.HomeDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PortableMarkerKeepsTheConfigBesideTheExecutable()
    {
        var directory = Directory.CreateTempSubdirectory("modernftp-tests-").FullName;
        try
        {
            Assert.NotEqual(Path.GetFullPath(directory), ConfigPaths.ResolveConfigDirectory(directory));
            File.WriteAllText(Path.Combine(directory, ConfigPaths.PortableMarker), string.Empty);
            Assert.Equal(Path.GetFullPath(directory), ConfigPaths.ResolveConfigDirectory(directory));
            Assert.Equal(Path.Combine(Path.GetFullPath(directory), ConfigPaths.ConfigFileName), ConfigPaths.DefaultConfigPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
