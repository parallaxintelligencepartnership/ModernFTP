namespace ModernFTP.Conformance;

/// <summary>Drives the engine with the system curl, the way real users will.</summary>
public class CurlConformanceTests(ServerFixture fx) : IClassFixture<ServerFixture>
{
    private string Url(string path = "") => fx.Url + path;

    private static void Ok(CurlResult result) =>
        Assert.True(result.ExitCode == 0, $"curl exited {result.ExitCode}: {result.StdErr}");

    [CurlFact]
    public async Task LoginAndPwd()
    {
        var result = await Curl.RunAsync("--verbose", "--user", fx.UserPass, Url());
        Ok(result);
        Assert.Contains("230 User logged in.", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("257 \"/\" is the current directory.", result.StdErr, StringComparison.Ordinal);
    }

    [CurlFact]
    public async Task UploadFile()
    {
        var bytes = ServerFixture.RandomBytes(150_000);
        var local = fx.WorkFile("upload.bin");
        await File.WriteAllBytesAsync(local, bytes);
        Ok(await Curl.RunAsync("--user", fx.UserPass, "--upload-file", local, Url("upload.bin")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fx.Home, "upload.bin")));
    }

    [CurlFact]
    public async Task DownloadFileByteForByte()
    {
        var bytes = ServerFixture.RandomBytes(333_333);
        await File.WriteAllBytesAsync(Path.Combine(fx.Home, "download.bin"), bytes);
        var local = fx.WorkFile("download.bin");
        Ok(await Curl.RunAsync("--user", fx.UserPass, "--output", local, Url("download.bin")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(local));
    }

    [CurlFact]
    public async Task ResumeDownloadAfterPartial()
    {
        var bytes = ServerFixture.RandomBytes(200_000);
        await File.WriteAllBytesAsync(Path.Combine(fx.Home, "resume-down.bin"), bytes);
        var local = fx.WorkFile("resume-down.bin");
        await File.WriteAllBytesAsync(local, bytes[..70_000]);
        var result = await Curl.RunAsync("--verbose", "--user", fx.UserPass, "--continue-at", "-", "--output", local, Url("resume-down.bin"));
        Ok(result);
        Assert.Contains("REST 70000", result.StdErr, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(local));
    }

    [CurlFact]
    public async Task ResumeUploadAfterPartial()
    {
        var bytes = ServerFixture.RandomBytes(200_000);
        var local = fx.WorkFile("resume-up.bin");
        await File.WriteAllBytesAsync(local, bytes);
        await File.WriteAllBytesAsync(Path.Combine(fx.Home, "resume-up.bin"), bytes[..80_000]);
        Ok(await Curl.RunAsync("--user", fx.UserPass, "--continue-at", "-", "--upload-file", local, Url("resume-up.bin")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fx.Home, "resume-up.bin")));
    }

    [CurlFact]
    public async Task ListAndMlsdShowTheFile()
    {
        await File.WriteAllTextAsync(Path.Combine(fx.Home, "listed.txt"), "hello");
        var list = await Curl.RunAsync("--user", fx.UserPass, Url());
        Ok(list);
        Assert.Matches(@"-rw-r--r-- .* 5 .* listed\.txt", list.StdOut);

        var mlsd = await Curl.RunAsync("--user", fx.UserPass, "--request", "MLSD", Url());
        Ok(mlsd);
        Assert.Contains("type=file;size=5;", mlsd.StdOut, StringComparison.Ordinal);
        Assert.Contains(" listed.txt", mlsd.StdOut, StringComparison.Ordinal);
    }

    [CurlFact]
    public async Task MkdRenameAndDeleteThroughQuote()
    {
        await File.WriteAllTextAsync(Path.Combine(fx.Home, "rename-me.txt"), "r");
        await File.WriteAllTextAsync(Path.Combine(fx.Home, "delete-me.txt"), "d");
        Ok(await Curl.RunAsync(
            "--user", fx.UserPass,
            "--quote", "MKD made-by-quote",
            "--quote", "RNFR rename-me.txt",
            "--quote", "RNTO made-by-quote/renamed.txt",
            "--quote", "DELE delete-me.txt",
            Url()));
        Assert.True(File.Exists(Path.Combine(fx.Home, "made-by-quote", "renamed.txt")));
        Assert.False(File.Exists(Path.Combine(fx.Home, "rename-me.txt")));
        Assert.False(File.Exists(Path.Combine(fx.Home, "delete-me.txt")));
    }

    [CurlFact]
    public async Task UploadDeniedWithoutUploadPermission()
    {
        var local = fx.WorkFile("denied.bin");
        await File.WriteAllBytesAsync(local, ServerFixture.RandomBytes(1000));
        var result = await Curl.RunAsync(
            "--user", $"{ServerFixture.ReaderUser}:{ServerFixture.ReaderPassword}", "--upload-file", local, Url("denied.bin"));
        Assert.Equal(25, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(fx.ReaderHome, "denied.bin")));
    }

    [CurlFact]
    public async Task PasvDataPortIsInsideConfiguredRange()
    {
        await File.WriteAllTextAsync(Path.Combine(fx.Home, "pasv.txt"), "pasv");
        for (var i = 0; i < 5; i++)
        {
            var result = await Curl.RunAsync("--verbose", "--disable-epsv", "--user", fx.UserPass, "--output", fx.WorkFile("pasv.txt"), Url("pasv.txt"));
            Ok(result);
            Assert.Contains("PASV", result.StdErr, StringComparison.Ordinal);
            var started = await fx.LastTransferStartedAsync("/pasv.txt", i + 1);
            Assert.InRange(started.DataPort, fx.PassiveMin, fx.PassiveMax);
        }
    }

    [CurlFact]
    public async Task ExplicitFtpsUploadAndDownload()
    {
        var bytes = ServerFixture.RandomBytes(250_000);
        var local = fx.WorkFile("tls-up.bin");
        await File.WriteAllBytesAsync(local, bytes);
        var upload = await Curl.RunAsync("--verbose", "--ftp-ssl-reqd", "--insecure", "--user", fx.UserPass, "--upload-file", local, Url("tls.bin"));
        Ok(upload);
        Assert.Contains("PROT P", upload.StdErr, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fx.Home, "tls.bin")));
        Assert.True((await fx.LastTransferStartedAsync("/tls.bin")).Secure);

        var downloaded = fx.WorkFile("tls-down.bin");
        Ok(await Curl.RunAsync("--ssl-reqd", "--insecure", "--user", fx.UserPass, "--output", downloaded, Url("tls.bin")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded));
    }

    [CurlFact]
    public async Task ActiveModeDownload()
    {
        var bytes = ServerFixture.RandomBytes(50_000);
        await File.WriteAllBytesAsync(Path.Combine(fx.Home, "active.bin"), bytes);
        var local = fx.WorkFile("active.bin");
        var result = await Curl.RunAsync("--verbose", "--ftp-port", "-", "--user", fx.UserPass, "--output", local, Url("active.bin"));
        Ok(result);
        Assert.Matches("EPRT|PORT", result.StdErr);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(local));
    }
}
