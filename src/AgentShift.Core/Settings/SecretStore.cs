// AgentShift - compare TV episode order between TVDB and TMDB
// Copyright (C) 2026 stark23x
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace AgentShift.Core.Settings;

/// <summary>Stores tokens and API keys in the operating system's secure store.</summary>
public interface ISecretStore
{
    /// <summary>Plain-language name of where secrets are kept, shown in Settings.</summary>
    string Description { get; }
    bool IsSecure { get; }
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}

public static class SecretStoreFactory
{
    public static ISecretStore Create(string? fallbackDirectory = null)
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainStore();
        if (OperatingSystem.IsLinux() && LinuxSecretToolStore.IsAvailable()) return new LinuxSecretToolStore();
        return new FileSecretStore(fallbackDirectory ?? AppPaths.ConfigDirectory);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ISecretStore
{
    private const string Prefix = "AgentShift:";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;

    public string Description => "Windows Credential Manager";
    public bool IsSecure => true;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string? Get(string key)
    {
        if (!CredRead(Prefix + key, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string key, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = Prefix + key,
                CredentialBlob = blob,
                CredentialBlobSize = bytes.Length,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "Saved by AgentShift",
            };
            if (!CredWrite(ref cred, 0))
                throw new InvalidOperationException($"Couldn't save to Windows Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string key) => CredDelete(Prefix + key, CredTypeGeneric, 0);
}

/// <summary>macOS Keychain through the built-in <c>security</c> tool. Secrets go over stdin, never the command line.</summary>
public sealed class MacKeychainStore : ISecretStore
{
    private const string Service = "AgentShift";
    public string Description => "macOS Keychain";
    public bool IsSecure => true;

    public string? Get(string key)
    {
        var (code, output) = ProcessRunner.Run("security", new[] { "find-generic-password", "-s", Service, "-a", key, "-w" }, null);
        return code == 0 ? output.TrimEnd('\n', '\r') : null;
    }

    public void Set(string key, string value)
    {
        // "security -i" reads commands from stdin, which keeps the secret out of the process list.
        var cmd = $"add-generic-password -U -s {Quote(Service)} -a {Quote(key)} -w {Quote(value)}\n";
        var (code, output) = ProcessRunner.Run("security", new[] { "-i" }, cmd);
        if (code != 0) throw new InvalidOperationException("Couldn't save to the macOS Keychain: " + output.Trim());
    }

    public void Delete(string key) =>
        ProcessRunner.Run("security", new[] { "delete-generic-password", "-s", Service, "-a", key }, null);

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

/// <summary>Linux keyring (GNOME Keyring, KWallet via libsecret) through <c>secret-tool</c>.</summary>
public sealed class LinuxSecretToolStore : ISecretStore
{
    public string Description => "system keyring (libsecret)";
    public bool IsSecure => true;

    public static bool IsAvailable()
    {
        try
        {
            var (code, _) = ProcessRunner.Run("secret-tool", new[] { "search", "service", "AgentShift-probe" }, null, timeoutMs: 5000);
            // 0 = found, 1 = nothing found; anything else (127 = missing, or no keyring daemon) = unusable.
            return code is 0 or 1;
        }
        catch
        {
            return false;
        }
    }

    public string? Get(string key)
    {
        var (code, output) = ProcessRunner.Run("secret-tool", new[] { "lookup", "service", "AgentShift", "account", key }, null);
        return code == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;
    }

    public void Set(string key, string value)
    {
        var (code, output) = ProcessRunner.Run("secret-tool",
            new[] { "store", "--label", $"AgentShift {key}", "service", "AgentShift", "account", key }, value);
        if (code != 0) throw new InvalidOperationException("Couldn't save to the system keyring: " + output.Trim());
    }

    public void Delete(string key) =>
        ProcessRunner.Run("secret-tool", new[] { "clear", "service", "AgentShift", "account", key }, null);
}

/// <summary>Fallback when no keyring exists: a JSON file only the current user can read.</summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public FileSecretStore(string directory) => _path = Path.Combine(directory, "secrets.json");

    public string Description => $"a private file ({_path}) because no system keyring was found";
    public bool IsSecure => false;

    public string? Get(string key)
    {
        lock (_gate) return Read().TryGetValue(key, out var v) ? v : null;
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var d = Read();
            d[key] = value;
            Write(d);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var d = Read();
            if (d.Remove(key)) Write(d);
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private void Write(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!OperatingSystem.IsWindows())
        {
            using var fs = new FileStream(_path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var sw = new StreamWriter(fs);
            sw.Write(JsonSerializer.Serialize(d));
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        else
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(d));
        }
    }
}

/// <summary>In-memory store, used by tests and the command line when secrets come from flags.</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _d = new();
    public string Description => "memory only";
    public bool IsSecure => true;
    public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string value) => _d[key] = value;
    public void Delete(string key) => _d.Remove(key);
}

internal static class ProcessRunner
{
    public static (int ExitCode, string Output) Run(string file, IEnumerable<string> args, string? stdin, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException($"Couldn't start {file}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (127, $"{file} was not found.");
        }

        using (p)
        {
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* ignore */ }
                return (124, "Timed out.");
            }
            var output = stdout.Result;
            return (p.ExitCode, p.ExitCode == 0 ? output : output + stderr.Result);
        }
    }
}
