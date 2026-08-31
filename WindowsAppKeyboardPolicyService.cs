using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RDPTools;

internal sealed partial class WindowsAppKeyboardPolicyService : IDisposable
{
    private const string PackageFamilyName = "MicrosoftCorporationII.Windows365_8wekyb3d8bbwe";
    private const string KeyboardHookProperty = "keyboardhook:i:1";

    private readonly ConcurrentDictionary<string, long> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failedWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _patchLock = new();
    private readonly string[] _cacheDirectories;
    private System.Threading.Timer? _retryTimer;
    private long _queueGeneration;
    private int _started;
    private int _disposed;

    internal WindowsAppKeyboardPolicyService()
    {
        var packageCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            PackageFamilyName,
            "LocalCache");
        _cacheDirectories =
        [
            Path.Combine(packageCache, "ResourceCache"),
            Path.Combine(packageCache, "LaunchFiles"),
        ];
    }

    internal void Start()
    {
        PatchPendingFiles();

        _retryTimer = new System.Threading.Timer(
            _ => PatchPendingFiles(),
            null,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250));
        Volatile.Write(ref _started, 1);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _started, 0);
        _retryTimer?.Dispose();
        lock (_patchLock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void QueueFile(object sender, FileSystemEventArgs eventArgs)
    {
        QueueFile(eventArgs.FullPath);
    }

    private void QueueFile(object sender, RenamedEventArgs eventArgs)
    {
        QueueFile(eventArgs.FullPath);
    }

    private void QueueFile(string path)
    {
        var generation = Interlocked.Increment(ref _queueGeneration);
        _pendingFiles.AddOrUpdate(path, generation, (_, _) => generation);
        if (Volatile.Read(ref _started) != 0)
        {
            ThreadPool.QueueUserWorkItem(_ => PatchPendingFiles());
        }
    }

    private void QueueDirectory(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.rdp"))
            {
                QueueFile(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void QueueWatcherReset(string directory)
    {
        _failedWatchers.TryAdd(directory, 0);
        if (Volatile.Read(ref _started) != 0)
        {
            ThreadPool.QueueUserWorkItem(_ => PatchPendingFiles());
        }
    }

    private void EnsureDirectoryWatchers()
    {
        foreach (var directory in _cacheDirectories)
        {
            if (_watchers.ContainsKey(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(directory, "*.rdp")
                {
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.CreationTime |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size,
                };
                watcher.Created += QueueFile;
                watcher.Changed += QueueFile;
                watcher.Renamed += QueueFile;
                watcher.Error += (_, _) => QueueWatcherReset(directory);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(directory, watcher);
                QueueDirectory(directory);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void ResetFailedWatchers()
    {
        foreach (var directory in _failedWatchers.Keys)
        {
            if (_watchers.Remove(directory, out var watcher))
            {
                watcher.Dispose();
            }

            _failedWatchers.TryRemove(directory, out _);
        }
    }

    private void PatchPendingFiles()
    {
        lock (_patchLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            ResetFailedWatchers();
            EnsureDirectoryWatchers();
            foreach (var pendingFile in _pendingFiles.ToArray())
            {
                if (TryPatchFile(pendingFile.Key))
                {
                    ((ICollection<KeyValuePair<string, long>>)_pendingFiles).Remove(pendingFile);
                }
            }
        }
    }

    private static bool TryPatchFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            if (stream.Length > int.MaxValue)
            {
                return true;
            }

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var source = DecodeTextFile(bytes);
            string? patchedText;
            if (source.Text.TrimStart().StartsWith('{'))
            {
                patchedText = PatchCachedResource(source.Text);
            }
            else
            {
                patchedText = PatchRdpPayload(source.Text);
            }

            if (patchedText is null || string.Equals(patchedText, source.Text, StringComparison.Ordinal))
            {
                return true;
            }

            var patchedBytes = EncodeTextFile(patchedText, source.Encoding, source.HasPreamble);
            stream.Position = 0;
            stream.Write(patchedBytes);
            stream.SetLength(patchedBytes.Length);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    private static string? PatchCachedResource(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject cache ||
            cache["cached_item"] is not JsonValue cachedItem ||
            !cachedItem.TryGetValue<string>(out var payload))
        {
            return null;
        }

        var patchedPayload = PatchRdpPayload(payload);
        if (patchedPayload is null || string.Equals(patchedPayload, payload, StringComparison.Ordinal))
        {
            return text;
        }

        cache["cached_item"] = patchedPayload;
        return cache.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static string? PatchRdpPayload(string text)
    {
        var signScopeMatches = SignScopeLine().Matches(text);
        var signatureMatches = SignatureLine().Matches(text);
        if (signScopeMatches.Count > 1 || signatureMatches.Count > 1)
        {
            return null;
        }

        var signScopeMatch = signScopeMatches.Count == 1 ? signScopeMatches[0] : null;
        if (signScopeMatch is not null &&
            signScopeMatch.Groups["scope"].Value
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("keyboardhook", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        if (signScopeMatch is null && signatureMatches.Count != 0)
        {
            return null;
        }

        var keyboardHookMatches = KeyboardHookLine().Matches(text);
        if (keyboardHookMatches.Count != 0)
        {
            if (keyboardHookMatches.Count == 1 && string.Equals(
                keyboardHookMatches[0].Value,
                KeyboardHookProperty,
                StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            var normalizedText = text;
            for (var index = keyboardHookMatches.Count - 1; index > 0; index--)
            {
                normalizedText = RemoveLine(
                    normalizedText,
                    keyboardHookMatches[index].Index,
                    keyboardHookMatches[index].Length);
            }

            return KeyboardHookLine().Replace(normalizedText, KeyboardHookProperty, 1);
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var insertionPoint = signScopeMatch is not null
            ? signScopeMatch.Index
            : signatureMatches.Count == 1
                ? signatureMatches[0].Index
                : text.Length;
        var prefix = insertionPoint == text.Length &&
            text.Length > 0 &&
            text[^1] is not ('\r' or '\n')
            ? newLine
            : string.Empty;
        return text.Insert(insertionPoint, $"{prefix}{KeyboardHookProperty}{newLine}");
    }

    private static string RemoveLine(string text, int index, int length)
    {
        var end = index + length;
        if (end + 1 < text.Length && text[end] == '\r' && text[end + 1] == '\n')
        {
            length += 2;
        }
        else if (end < text.Length && text[end] == '\n')
        {
            length++;
        }

        return text.Remove(index, length);
    }

    private static TextFile DecodeTextFile(byte[] bytes)
    {
        Encoding encoding;
        var preambleLength = 0;
        var utf32BigEndian = new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);
        if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()))
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
            preambleLength = Encoding.UTF32.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(utf32BigEndian.GetPreamble()))
        {
            encoding = utf32BigEndian;
            preambleLength = utf32BigEndian.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
            preambleLength = Encoding.UTF8.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            preambleLength = Encoding.Unicode.GetPreamble().Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
            preambleLength = Encoding.BigEndianUnicode.GetPreamble().Length;
        }
        else if (LooksLikeUtf16(bytes, oddBytesAreNull: true))
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
        }
        else if (LooksLikeUtf16(bytes, oddBytesAreNull: false))
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        }

        return new TextFile(
            encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength),
            encoding,
            preambleLength > 0);
    }

    private static byte[] EncodeTextFile(
        string text,
        Encoding encoding,
        bool includePreamble)
    {
        var content = encoding.GetBytes(text);
        var preamble = includePreamble ? encoding.GetPreamble() : [];
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static bool LooksLikeUtf16(byte[] bytes, bool oddBytesAreNull)
    {
        if (bytes.Length < 4 || bytes.Length % 2 != 0)
        {
            return false;
        }

        var pairs = Math.Min(bytes.Length / 2, 128);
        var nullCount = 0;
        for (var index = 0; index < pairs; index++)
        {
            var byteIndex = index * 2 + (oddBytesAreNull ? 1 : 0);
            if (bytes[byteIndex] == 0)
            {
                nullCount++;
            }
        }

        return nullCount >= pairs * 3 / 4;
    }

    [GeneratedRegex("^keyboardhook:[^\\r\\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex KeyboardHookLine();

    [GeneratedRegex("^signscope:s:(?<scope>[^\\r\\n]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SignScopeLine();

    [GeneratedRegex("^signature:[^\\r\\n]*", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SignatureLine();

    private readonly record struct TextFile(string Text, Encoding Encoding, bool HasPreamble);
}