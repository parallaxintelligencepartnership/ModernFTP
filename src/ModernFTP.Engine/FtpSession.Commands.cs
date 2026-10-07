using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace ModernFTP.Engine;

internal sealed partial class FtpSession
{
    private Dictionary<string, CommandSpec> BuildCommandTable()
    {
        var table = new Dictionary<string, CommandSpec>(StringComparer.Ordinal);

        void Add(string verb, CommandHandler handler, bool requiresLogin = true, bool duringTransfer = false) =>
            table[verb] = new CommandSpec(handler, requiresLogin, duringTransfer);

        Add("USER", UserAsync, requiresLogin: false);
        Add("PASS", PassAsync, requiresLogin: false);
        Add("QUIT", QuitAsync, requiresLogin: false);
        Add("SYST", SystAsync, requiresLogin: false);
        Add("FEAT", FeatAsync, requiresLogin: false);
        Add("OPTS", OptsAsync, requiresLogin: false);
        Add("NOOP", NoopAsync, requiresLogin: false, duringTransfer: true);
        Add("AUTH", AuthAsync, requiresLogin: false);
        Add("PBSZ", PbszAsync, requiresLogin: false);
        Add("PROT", ProtAsync, requiresLogin: false);
        Add("PWD", PwdAsync);
        Add("CWD", CwdAsync);
        Add("CDUP", CdupAsync);
        Add("TYPE", TypeAsync);
        Add("MODE", ModeAsync);
        Add("STRU", StruAsync);
        Add("PASV", PasvAsync);
        Add("EPSV", EpsvAsync);
        Add("PORT", PortAsync);
        Add("EPRT", EprtAsync);
        Add("LIST", arg => ListAsync(arg, ListFormat.Long));
        Add("NLST", arg => ListAsync(arg, ListFormat.Names));
        Add("MLSD", arg => ListAsync(arg, ListFormat.Machine));
        Add("MLST", MlstAsync);
        Add("RETR", RetrAsync);
        Add("STOR", arg => StoreAsync(arg, append: false));
        Add("APPE", arg => StoreAsync(arg, append: true));
        Add("REST", RestAsync);
        Add("SIZE", SizeAsync);
        Add("MDTM", MdtmAsync);
        Add("DELE", DeleAsync);
        Add("MKD", MkdAsync);
        Add("RMD", RmdAsync);
        Add("RNFR", RnfrAsync);
        Add("RNTO", RntoAsync);
        Add("ABOR", AborAsync, duringTransfer: true);
        Add("STAT", StatAsync, duringTransfer: true);
        return table;
    }

    private enum ListFormat
    {
        Long,
        Names,
        Machine,
    }

    // ---- Login -------------------------------------------------------------------------------

    private async Task<bool> UserAsync(string argument)
    {
        if (_user is not null)
        {
            await ReplyAsync(503, "Already logged in.").ConfigureAwait(false);
            return true;
        }

        var name = argument.Trim();
        if (name.Length == 0 || name.Length > 128)
        {
            await ReplyAsync(501, "Syntax error in parameters or arguments.").ConfigureAwait(false);
            return true;
        }

        _pendingUser = name;
        await ReplyAsync(331, "Password required.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> PassAsync(string argument)
    {
        if (_user is not null)
        {
            await ReplyAsync(503, "Already logged in.").ConfigureAwait(false);
            return true;
        }

        if (_pendingUser is not { } name)
        {
            await ReplyAsync(503, "Send USER first.").ConfigureAwait(false);
            return true;
        }

        _pendingUser = null;
        var throttle = _server.LoginThrottle;
        if (throttle.IsBanned(RemoteAddress))
        {
            _closeReason = "address temporarily banned after failed logins";
            await ReplyAsync(421, "Too many failed login attempts, try again later.").ConfigureAwait(false);
            return false;
        }

        if (_options.LoginFailureLimit > 0
            && _options.LoginThrottleDelay > TimeSpan.Zero
            && throttle.RecentFailures(RemoteAddress, _options.LoginFailureWindow) >= _options.LoginFailureLimit)
        {
            await Task.Delay(_options.LoginThrottleDelay, _cts.Token).ConfigureAwait(false);
        }

        var user = _server.FindUser(name);
        bool valid;
        if (user is { Enabled: true })
        {
            valid = user.Credential.Verify(argument);
        }
        else
        {
            // Same PBKDF2 work for unknown and disabled names, so the time does not reveal which names exist.
            Pbkdf2Credential.Dummy.Verify(argument);
            valid = false;
        }

        if (!valid || user is null)
        {
            if (throttle.RecordFailure(RemoteAddress, _options))
            {
                PublishError($"address {RemoteAddress} banned for {_options.LoginBanDuration.TotalMinutes:0.#} minutes after failed logins");
            }

            _failedLogins++;
            PublishError($"failed login for '{Sanitize(name)}'");
            if (_options.FailedLoginDelay > TimeSpan.Zero)
            {
                await Task.Delay(_options.FailedLoginDelay, _cts.Token).ConfigureAwait(false);
            }

            if (_options.MaxFailedLogins > 0 && _failedLogins >= _options.MaxFailedLogins)
            {
                _closeReason = "too many failed logins";
                await ReplyAsync(421, "Too many failed login attempts, closing control connection.").ConfigureAwait(false);
                return false;
            }

            await ReplyAsync(530, "Login incorrect.").ConfigureAwait(false);
            return true;
        }

        if (!Directory.Exists(user.HomeDirectory))
        {
            PublishError($"home directory for '{user.UserName}' does not exist");
            await ReplyAsync(530, "Home directory is not available.").ConfigureAwait(false);
            return true;
        }

        if (_server.TryAcquireUserSlot(user, RemoteEndPoint.Address) is { } refused)
        {
            _closeReason = refused;
            await ReplyAsync(421, "Too many connections for this user, try again later.").ConfigureAwait(false);
            return false;
        }

        Interlocked.Exchange(ref _user, user);
        _server.MarkAuthenticated(RemoteEndPoint.Address);

        // The user table may have been replaced since the lookup above (FtpServer.ApplyOptions).
        if (_server.FindUser(user.UserName) is not { Enabled: true } current)
        {
            _closeReason = "account disabled or removed";
            await ReplyAsync(421, "Your account was disabled or removed.").ConfigureAwait(false);
            return false;
        }

        if (!ReferenceEquals(current, user))
        {
            ReplaceUser(current);
            user = current;
        }

        _cwd = VirtualPath.Root;
        _server.Publish(new AuthenticatedEvent { SessionId = Id, RemoteEndPoint = RemoteEndPoint, UserName = user.UserName });
        await ReplyAsync(230, "User logged in.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> QuitAsync(string argument)
    {
        _closeReason = "client quit";
        _quit = true;
        var lines = SplitMessage(_options.GoodbyeMessage).ToList();
        await ReplyAsync(221, lines.Count == 0 ? "Goodbye." : string.Join('\n', lines)).ConfigureAwait(false);
        return false;
    }

    // ---- Informational -----------------------------------------------------------------------

    private async Task<bool> SystAsync(string argument)
    {
        await ReplyAsync(215, "UNIX Type: L8").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> FeatAsync(string argument)
    {
        var builder = new StringBuilder("211-Features:\r\n");
        if (_server.TlsAvailable)
        {
            builder.Append(" AUTH TLS\r\n PBSZ\r\n PROT\r\n");
        }

        builder.Append(" EPRT\r\n EPSV\r\n MDTM\r\n MLST type*;size*;modify*;perm*;\r\n PASV\r\n REST STREAM\r\n SIZE\r\n TVFS\r\n UTF8\r\n");
        builder.Append("211 End\r\n");
        await WriteRawAsync(builder.ToString()).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> OptsAsync(string argument)
    {
        var option = argument.Trim().ToUpperInvariant();
        if (option is "UTF8 ON" or "UTF8" or "UTF-8 ON")
        {
            await ReplyAsync(200, "UTF8 mode is always on.").ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(501, "Option not understood.").ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> NoopAsync(string argument)
    {
        await ReplyAsync(200, "NOOP ok.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> StatAsync(string argument)
    {
        if (argument.Trim().Length > 0)
        {
            await ReplyAsync(504, "STAT with an argument is not supported.").ConfigureAwait(false);
            return true;
        }

        var lines = new[]
        {
            $"{(_options.HideServerName ? "FTP" : FtpServer.ServerName)} status:",
            $"Connected from {RemoteAddress}",
            $"Logged in as {_user?.UserName}",
            $"TYPE: {(_binary ? "BINARY" : "ASCII")}",
            $"Control connection: {(_tls ? "TLS" : "plain")}",
            _transfer is { IsCompleted: false } ? "Data transfer in progress" : "No data transfer in progress",
            "End of status",
        };
        await ReplyAsync(211, string.Join('\n', lines)).ConfigureAwait(false);
        return true;
    }

    // ---- TLS ---------------------------------------------------------------------------------

    private async Task<bool> AuthAsync(string argument)
    {
        var mechanism = argument.Trim().ToUpperInvariant();
        if (!_server.TlsAvailable)
        {
            await ReplyAsync(431, "TLS is not available on this server.").ConfigureAwait(false);
            return true;
        }

        if (_tls)
        {
            await ReplyAsync(503, "TLS is already active.").ConfigureAwait(false);
            return true;
        }

        if (mechanism is not ("TLS" or "TLS-C" or "SSL" or "TLS-P"))
        {
            await ReplyAsync(504, "Unsupported security mechanism.").ConfigureAwait(false);
            return true;
        }

        await ReplyAsync(234, $"AUTH {mechanism} successful.").ConfigureAwait(false);
        _reader.Reset();
        try
        {
            _control = await AuthenticateTlsAsync(_network, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or OperationCanceledException && !_cts.IsCancellationRequested)
        {
            // The failed SslStream is disposed, so nothing can be written back; end the session with the real reason.
            _closeReason = "TLS negotiation failed";
            PublishError($"TLS negotiation failed: {ex.Message}");
            return false;
        }

        _tls = true;
        _pendingUser = null;
        return true;
    }

    private async Task<bool> PbszAsync(string argument)
    {
        if (!_tls)
        {
            await ReplyAsync(503, "PBSZ requires AUTH first.").ConfigureAwait(false);
            return true;
        }

        _pbszReceived = true;
        await ReplyAsync(200, "PBSZ=0").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ProtAsync(string argument)
    {
        if (!_tls)
        {
            await ReplyAsync(503, "PROT requires AUTH first.").ConfigureAwait(false);
            return true;
        }

        switch (argument.Trim().ToUpperInvariant())
        {
            case "C":
                _protectData = false;
                await ReplyAsync(200, "Protection level set to Clear.").ConfigureAwait(false);
                break;
            case "P":
                if (!_pbszReceived)
                {
                    await ReplyAsync(503, "PROT P requires PBSZ first.").ConfigureAwait(false);
                    break;
                }

                _protectData = true;
                await ReplyAsync(200, "Protection level set to Private.").ConfigureAwait(false);
                break;
            case "S" or "E":
                await ReplyAsync(536, "Protection level not supported.").ConfigureAwait(false);
                break;
            default:
                await ReplyAsync(504, "Unknown protection level.").ConfigureAwait(false);
                break;
        }

        return true;
    }

    // ---- Navigation and parameters -----------------------------------------------------------

    private async Task<bool> PwdAsync(string argument)
    {
        await ReplyAsync(257, $"\"{_cwd.ToString().Replace("\"", "\"\"", StringComparison.Ordinal)}\" is the current directory.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> CwdAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || !Directory.Exists(Physical(path)))
        {
            await ReplyAsync(550, "No such directory.").ConfigureAwait(false);
            return true;
        }

        _cwd = path;
        await ReplyAsync(250, $"Directory changed to {path}.").ConfigureAwait(false);
        return true;
    }

    private Task<bool> CdupAsync(string argument) => CwdAsync("..");

    private async Task<bool> TypeAsync(string argument)
    {
        switch (argument.Trim().ToUpperInvariant())
        {
            case "A" or "A N":
                _binary = false;
                await ReplyAsync(200, "Type set to A.").ConfigureAwait(false);
                break;
            case "I" or "L 8":
                _binary = true;
                await ReplyAsync(200, "Type set to I.").ConfigureAwait(false);
                break;
            default:
                await ReplyAsync(504, "Type not supported.").ConfigureAwait(false);
                break;
        }

        return true;
    }

    private async Task<bool> ModeAsync(string argument)
    {
        var ok = argument.Trim().Equals("S", StringComparison.OrdinalIgnoreCase);
        await ReplyAsync(ok ? 200 : 504, ok ? "Mode set to S." : "Only stream mode is supported.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> StruAsync(string argument)
    {
        var ok = argument.Trim().Equals("F", StringComparison.OrdinalIgnoreCase);
        await ReplyAsync(ok ? 200 : 504, ok ? "Structure set to F." : "Only file structure is supported.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RestAsync(string argument)
    {
        if (!long.TryParse(argument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            await ReplyAsync(501, "Invalid restart offset.").ConfigureAwait(false);
            return true;
        }

        _restartOffset = offset;
        await ReplyAsync(350, $"Restarting at {offset}. Send STOR or RETR to start the transfer.").ConfigureAwait(false);
        return true;
    }

    // ---- Data connection setup ---------------------------------------------------------------

    private async Task<bool> PasvAsync(string argument)
    {
        if (_epsvAll)
        {
            await ReplyAsync(503, "PASV is not allowed after EPSV ALL.").ConfigureAwait(false);
            return true;
        }

        var advertised = _options.PassivePublicAddress ?? NetUtil.Normalize(LocalEndPoint.Address);
        if (advertised.AddressFamily != AddressFamily.InterNetwork)
        {
            await ReplyAsync(425, "Use EPSV on IPv6 connections.").ConfigureAwait(false);
            return true;
        }

        var channel = OpenPassive();
        if (channel is null)
        {
            await ReplyAsync(425, "No passive port is available.").ConfigureAwait(false);
            return true;
        }

        var b = advertised.GetAddressBytes();
        await ReplyAsync(227, $"Entering Passive Mode ({b[0]},{b[1]},{b[2]},{b[3]},{channel.Port >> 8},{channel.Port & 0xFF}).").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> EpsvAsync(string argument)
    {
        if (argument.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            _epsvAll = true;
            await ReplyAsync(200, "EPSV ALL accepted.").ConfigureAwait(false);
            return true;
        }

        var channel = OpenPassive();
        if (channel is null)
        {
            await ReplyAsync(425, "No passive port is available.").ConfigureAwait(false);
            return true;
        }

        await ReplyAsync(229, $"Entering Extended Passive Mode (|||{channel.Port}|).").ConfigureAwait(false);
        return true;
    }

    private PassiveDataChannel? OpenPassive()
    {
        ReplaceDataChannel(null);
        var channel = _server.PassivePorts.TryOpen(LocalEndPoint.Address, RemoteAddress);
        _dataChannel = channel;
        return channel;
    }

    private async Task<bool> PortAsync(string argument)
    {
        var parts = argument.Trim().Split(',');
        var numbers = new byte[6];
        var valid = parts.Length == 6;
        for (var i = 0; valid && i < 6; i++)
        {
            valid = byte.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]);
        }

        if (!valid)
        {
            await ReplyAsync(501, "Syntax error in PORT parameters.").ConfigureAwait(false);
            return true;
        }

        var address = new IPAddress(numbers.AsSpan(0, 4));
        return await SetActiveAsync(address, (numbers[4] << 8) | numbers[5], "PORT").ConfigureAwait(false);
    }

    private async Task<bool> EprtAsync(string argument)
    {
        var text = argument.Trim();
        if (text.Length < 7)
        {
            await ReplyAsync(501, "Syntax error in EPRT parameters.").ConfigureAwait(false);
            return true;
        }

        var parts = text.Split(text[0]);
        if (parts.Length != 5
            || parts[1] is not ("1" or "2")
            || !IPAddress.TryParse(parts[2], out var address)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535
            || (parts[1] == "1") != (address.AddressFamily == AddressFamily.InterNetwork))
        {
            await ReplyAsync(501, "Syntax error in EPRT parameters.").ConfigureAwait(false);
            return true;
        }

        return await SetActiveAsync(address, port, "EPRT").ConfigureAwait(false);
    }

    private async Task<bool> SetActiveAsync(IPAddress address, int port, string verb)
    {
        if (!_options.AllowActiveMode)
        {
            await ReplyAsync(502, "Active mode is disabled on this server.").ConfigureAwait(false);
            return true;
        }

        if (_epsvAll)
        {
            await ReplyAsync(503, $"{verb} is not allowed after EPSV ALL.").ConfigureAwait(false);
            return true;
        }

        // Bounce protection: the data connection may only go back to the client itself, on an unprivileged port.
        if (!NetUtil.Normalize(address).Equals(RemoteAddress) || port < 1024)
        {
            await ReplyAsync(501, $"{verb} must name the client's own address and a port above 1023.").ConfigureAwait(false);
            return true;
        }

        ReplaceDataChannel(new ActiveDataChannel(new IPEndPoint(address, port)));
        await ReplyAsync(200, $"{verb} command successful.").ConfigureAwait(false);
        return true;
    }

    // ---- Transfers ---------------------------------------------------------------------------

    private async Task<bool> RetrAsync(string argument)
    {
        var offset = TakeRestartOffset();
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid file name.").ConfigureAwait(false);
            return true;
        }

        if (!Permissions(path).Download)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        if (!File.Exists(physical))
        {
            await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            return true;
        }

        FileStream file;
        try
        {
            file = OpenFile(physical, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReplyAsync(550, "File is not available.").ConfigureAwait(false);
            return true;
        }

        if (offset > file.Length)
        {
            await file.DisposeAsync().ConfigureAwait(false);
            await ReplyAsync(554, "Restart offset is beyond the end of the file.").ConfigureAwait(false);
            return true;
        }

        file.Position = offset;
        var rate = (long)_user!.DownloadRateKBps * 1024;
        return await StartTransferAsync(
            TransferDirection.Download,
            path,
            (data, token) => CopyAsync(file, rate > 0 ? new ThrottledStream(data, rate, Touch) : data, token),
            file,
            file.Length - offset).ConfigureAwait(false);
    }

    private async Task<bool> StoreAsync(string argument, bool append)
    {
        var offset = append ? 0 : TakeRestartOffset();
        if (append)
        {
            _restartOffset = 0;
        }

        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid file name.").ConfigureAwait(false);
            return true;
        }

        var permissions = Permissions(path);
        if (!permissions.Upload)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        if (Directory.Exists(physical))
        {
            await ReplyAsync(550, "A directory with that name exists.").ConfigureAwait(false);
            return true;
        }

        if (!Directory.Exists(Path.GetDirectoryName(physical)))
        {
            await ReplyAsync(550, "The target directory does not exist.").ConfigureAwait(false);
            return true;
        }

        // Replacing an existing file destroys it, from the start or from a REST offset (STOR truncates
        // at the offset), so both need Delete. APPE never truncates and ignores REST.
        var exists = File.Exists(physical);
        if (!append && exists && !permissions.Delete)
        {
            await ReplyAsync(550, "Permission denied: overwriting needs delete permission.").ConfigureAwait(false);
            return true;
        }

        if (!append && offset > 0 && offset > (exists ? new FileInfo(physical).Length : 0))
        {
            await ReplyAsync(554, "Restart offset is beyond the end of the file.").ConfigureAwait(false);
            return true;
        }

        FileStream file;
        try
        {
            if (append)
            {
                file = OpenFile(physical, FileMode.Append, FileAccess.Write, FileShare.None);
            }
            else if (offset > 0)
            {
                file = OpenFile(physical, FileMode.Open, FileAccess.Write, FileShare.None);
                if (offset > file.Length)
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    await ReplyAsync(554, "Restart offset is beyond the end of the file.").ConfigureAwait(false);
                    return true;
                }

                file.SetLength(offset);
                file.Position = offset;
            }
            else
            {
                file = OpenFile(physical, FileMode.Create, FileAccess.Write, FileShare.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReplyAsync(550, "File is busy or not writable.").ConfigureAwait(false);
            return true;
        }

        return await StartTransferAsync(
            TransferDirection.Upload,
            path,
            (data, token) => CopyAsync(data, file, token),
            file,
            null).ConfigureAwait(false);
    }

    private async Task<bool> ListAsync(string argument, ListFormat format)
    {
        var target = StripListOptions(argument);
        if (!VirtualPath.TryResolve(_cwd, target, out var path))
        {
            await ReplyAsync(550, "Invalid path.").ConfigureAwait(false);
            return true;
        }

        var permissions = Permissions(path);
        if (!permissions.List)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        FileSystemInfo[] entries;
        if (Directory.Exists(physical))
        {
            entries = [.. new DirectoryInfo(physical).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal)];
        }
        else if (format != ListFormat.Machine && File.Exists(physical))
        {
            entries = [new FileInfo(physical)];
        }
        else
        {
            await ReplyAsync(550, "No such directory.").ConfigureAwait(false);
            return true;
        }

        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            if (!VirtualPath.IsValidName(entry.Name))
            {
                continue; // Names the client could never address are not shown.
            }

            builder.Append(format switch
            {
                ListFormat.Names => entry.Name,
                ListFormat.Machine => $"{Facts(entry, permissions)} {entry.Name}",
                _ => LongFormat(entry),
            }).Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        return await StartTransferAsync(
            TransferDirection.Listing,
            path,
            async (data, token) =>
            {
                await data.WriteAsync(bytes, token).ConfigureAwait(false);
                return bytes.Length;
            },
            null,
            bytes.Length).ConfigureAwait(false);
    }

    private async Task<bool> MlstAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path))
        {
            await ReplyAsync(550, "Invalid path.").ConfigureAwait(false);
            return true;
        }

        var permissions = Permissions(path);
        if (!permissions.List)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        FileSystemInfo? info = Directory.Exists(physical) ? new DirectoryInfo(physical)
            : File.Exists(physical) ? new FileInfo(physical)
            : null;
        if (info is null)
        {
            await ReplyAsync(550, "No such file or directory.").ConfigureAwait(false);
            return true;
        }

        await WriteRawAsync($"250-Listing {path}\r\n {Facts(info, permissions)} {path}\r\n250 End\r\n").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> AborAsync(string argument)
    {
        if (_transfer is { IsCompleted: false })
        {
            await AbortTransferAsync().ConfigureAwait(false);
            await ReplyAsync(226, "Abort successful.").ConfigureAwait(false);
            return true;
        }

        await WaitForTransferAsync().ConfigureAwait(false);
        ReplaceDataChannel(null);
        _restartOffset = 0;
        await ReplyAsync(226, "No transfer to abort.").ConfigureAwait(false);
        return true;
    }

    // ---- File and directory management -------------------------------------------------------

    private async Task<bool> SizeAsync(string argument)
    {
        var file = await ResolveExistingFileAsync(argument).ConfigureAwait(false);
        if (file is not null)
        {
            await ReplyAsync(213, file.Length.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> MdtmAsync(string argument)
    {
        var file = await ResolveExistingFileAsync(argument).ConfigureAwait(false);
        if (file is not null)
        {
            await ReplyAsync(213, file.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        return true;
    }

    private async Task<FileInfo?> ResolveExistingFileAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid file name.").ConfigureAwait(false);
            return null;
        }

        if (!Permissions(path).List)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return null;
        }

        var info = new FileInfo(Physical(path));
        if (!info.Exists)
        {
            await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            return null;
        }

        return info;
    }

    private async Task<bool> DeleAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid file name.").ConfigureAwait(false);
            return true;
        }

        if (!Permissions(path).Delete)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        if (!File.Exists(physical))
        {
            await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            return true;
        }

        try
        {
            File.Delete(physical);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReplyAsync(550, "File is busy or cannot be deleted.").ConfigureAwait(false);
            return true;
        }

        await ReplyAsync(250, "File deleted.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> MkdAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid directory name.").ConfigureAwait(false);
            return true;
        }

        if (!Permissions(path).MakeDir)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        if (Directory.Exists(physical) || File.Exists(physical))
        {
            await ReplyAsync(550, "A file or directory with that name already exists.").ConfigureAwait(false);
            return true;
        }

        if (!Directory.Exists(Path.GetDirectoryName(physical)))
        {
            await ReplyAsync(550, "The parent directory does not exist.").ConfigureAwait(false);
            return true;
        }

        Directory.CreateDirectory(physical);
        await ReplyAsync(257, $"\"{path.ToString().Replace("\"", "\"\"", StringComparison.Ordinal)}\" created.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RmdAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid directory name.").ConfigureAwait(false);
            return true;
        }

        if (!Permissions(path).RemoveDir)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        if (!Directory.Exists(physical))
        {
            await ReplyAsync(550, "No such directory.").ConfigureAwait(false);
            return true;
        }

        try
        {
            Directory.Delete(physical, recursive: false);
        }
        catch (IOException)
        {
            await ReplyAsync(550, "Directory is not empty or is in use.").ConfigureAwait(false);
            return true;
        }

        if (IsSameOrInside(_cwd, path))
        {
            _cwd = path.Parent;
        }

        await ReplyAsync(250, "Directory removed.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RnfrAsync(string argument)
    {
        if (!VirtualPath.TryResolve(_cwd, argument, out var path) || path.IsRoot)
        {
            await ReplyAsync(550, "Invalid name.").ConfigureAwait(false);
            return true;
        }

        var physical = Physical(path);
        var isDirectory = Directory.Exists(physical);
        if (!isDirectory && !File.Exists(physical))
        {
            await ReplyAsync(550, "File not found.").ConfigureAwait(false);
            return true;
        }

        var permissions = Permissions(path);
        if (!permissions.Rename)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        _renameFrom = path;
        await ReplyAsync(350, "Ready for RNTO.").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> RntoAsync(string argument)
    {
        if (_renameSource is not { } source)
        {
            await ReplyAsync(503, "Send RNFR first.").ConfigureAwait(false);
            return true;
        }

        if (!VirtualPath.TryResolve(_cwd, argument, out var target) || target.IsRoot)
        {
            await ReplyAsync(553, "Invalid target name.").ConfigureAwait(false);
            return true;
        }

        var sourcePhysical = Physical(source);
        var targetPhysical = Physical(target);
        var isDirectory = Directory.Exists(sourcePhysical);
        if (!isDirectory && !File.Exists(sourcePhysical))
        {
            await ReplyAsync(550, "Source no longer exists.").ConfigureAwait(false);
            return true;
        }

        var permissions = Permissions(target);
        if (!permissions.Rename)
        {
            await ReplyAsync(550, "Permission denied.").ConfigureAwait(false);
            return true;
        }

        if (File.Exists(targetPhysical) || Directory.Exists(targetPhysical))
        {
            await ReplyAsync(553, "Target already exists.").ConfigureAwait(false);
            return true;
        }

        if (isDirectory && IsSameOrInside(target, source))
        {
            await ReplyAsync(553, "Cannot move a directory into itself.").ConfigureAwait(false);
            return true;
        }

        if (!Directory.Exists(Path.GetDirectoryName(targetPhysical)))
        {
            await ReplyAsync(553, "The target directory does not exist.").ConfigureAwait(false);
            return true;
        }

        try
        {
            if (isDirectory)
            {
                Directory.Move(sourcePhysical, targetPhysical);
            }
            else
            {
                File.Move(sourcePhysical, targetPhysical);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await ReplyAsync(550, "Rename failed.").ConfigureAwait(false);
            return true;
        }

        await ReplyAsync(250, "Rename successful.").ConfigureAwait(false);
        return true;
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private FtpPermissions Permissions(VirtualPath path) => _user!.Permissions.Resolve(path);

    private string Physical(VirtualPath path) => path.ToPhysical(_user!.HomeDirectory);

    private static bool IsSameOrInside(VirtualPath candidate, VirtualPath ancestor) =>
        candidate.Segments.Count >= ancestor.Segments.Count
        && candidate.Segments.Take(ancestor.Segments.Count).SequenceEqual(ancestor.Segments, StringComparer.Ordinal);

    private static FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share) =>
        new(path, new FileStreamOptions
        {
            Mode = mode,
            Access = access,
            Share = share,
            Options = FileOptions.Asynchronous,
            BufferSize = 0,
        });

    private static string StripListOptions(string argument)
    {
        var text = argument.Trim();
        while (text.StartsWith('-'))
        {
            var space = text.IndexOf(' ');
            text = space < 0 ? string.Empty : text[(space + 1)..].TrimStart();
        }

        return text;
    }

    private static string LongFormat(FileSystemInfo entry)
    {
        var isDirectory = entry is DirectoryInfo;
        var size = entry is FileInfo file ? file.Length : 0;
        var modified = entry.LastWriteTimeUtc;
        var age = DateTime.UtcNow - modified;
        var date = age.TotalDays > 180 || age < TimeSpan.Zero
            ? modified.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture)
            : modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        return $"{(isDirectory ? "drwxr-xr-x" : "-rw-r--r--")} 1 ftp ftp {size,13} {date} {entry.Name}";
    }

    private static string Facts(FileSystemInfo entry, FtpPermissions permissions)
    {
        var modify = entry.LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (entry is FileInfo file)
        {
            var perm = new StringBuilder();
            if (permissions.Upload)
            {
                perm.Append("aw");
            }

            if (permissions.Delete)
            {
                perm.Append('d');
            }

            if (permissions.Rename)
            {
                perm.Append('f');
            }

            if (permissions.Download)
            {
                perm.Append('r');
            }

            return $"type=file;size={file.Length};modify={modify};perm={perm};";
        }

        var dirPerm = new StringBuilder("e");
        if (permissions.Upload)
        {
            dirPerm.Append('c');
        }

        if (permissions.RemoveDir)
        {
            dirPerm.Append("dp");
        }

        if (permissions.Rename)
        {
            dirPerm.Append('f');
        }

        if (permissions.List)
        {
            dirPerm.Append('l');
        }

        if (permissions.MakeDir)
        {
            dirPerm.Append('m');
        }

        return $"type=dir;modify={modify};perm={dirPerm};";
    }
}
