// Legacy edition only (.NET 6, Windows 7 and 8.1): the members of the .NET 7-10 base library the code uses, written
// on top of what .NET 6 has. Compiled into Custos.Core and public, so the service and the interface get them too.
// Most are C# 14 extension members, so the code calls them exactly as on .NET 10 (ArgumentException.ThrowIf...,
// Convert.ToHexStringLower, stream.ReadExactly). The standard edition never compiles this file.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

#pragma warning disable CA1034 // nested types: Lock.Scope must be nested, as in .NET 9
#pragma warning disable CA1815 // Scope: equality makes no sense, as in .NET 9
#pragma warning disable CA1711 // the names are the ones of .NET
#pragma warning disable CA1708 // C# 14 extension blocks of several types in one class

namespace System.Threading
{
    /// <summary>The <c>Lock</c> of .NET 9: the compiler turns <c>lock (x)</c> on it into <c>using (x.EnterScope())</c>.</summary>
    public sealed class Lock
    {
        private readonly object _monitor = new();

        public Scope EnterScope()
        {
            Monitor.Enter(_monitor);
            return new Scope(_monitor);
        }

        public void Enter() => Monitor.Enter(_monitor);

        public void Exit() => Monitor.Exit(_monitor);

        public bool TryEnter() => Monitor.TryEnter(_monitor);

        public bool IsHeldByCurrentThread => Monitor.IsEntered(_monitor);

        public ref struct Scope
        {
            private object? _monitor;

            internal Scope(object monitor) => _monitor = monitor;

            public void Dispose()
            {
                var monitor = _monitor;
                if (monitor is not null)
                {
                    _monitor = null;
                    Monitor.Exit(monitor);
                }
            }
        }
    }
}

namespace System.Threading.Tasks
{
    /// <summary>The <c>ConfigureAwaitOptions</c> of .NET 8.</summary>
    [Flags]
    public enum ConfigureAwaitOptions
    {
        None = 0,
        ContinueOnCapturedContext = 1,
        SuppressThrowing = 2,
        ForceYielding = 4,
    }
}

namespace System.Buffers
{
    /// <summary>The <c>SearchValues</c> of .NET 8, as a plain set of values.</summary>
    public sealed class SearchValues<T>
        where T : IEquatable<T>
    {
        internal SearchValues(T[] values) => Values = values;

        internal T[] Values { get; }

        public bool Contains(T value) => Array.IndexOf(Values, value) >= 0;
    }

    public static class SearchValues
    {
        public static SearchValues<char> Create(string values) => new((values ?? throw new ArgumentNullException(nameof(values))).ToCharArray());

        public static SearchValues<char> Create(ReadOnlySpan<char> values) => new(values.ToArray());

        public static SearchValues<byte> Create(ReadOnlySpan<byte> values) => new(values.ToArray());
    }
}

namespace System
{
    public static class LegacyApis
    {
        extension(ArgumentOutOfRangeException)
        {
            public static void ThrowIfNegative(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "Must not be negative.");
                }
            }

            public static void ThrowIfNegative(double value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "Must not be negative.");
                }
            }

            public static void ThrowIfNegativeOrZero(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "Must be positive.");
                }
            }

            public static void ThrowIfNegativeOrZero(double value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "Must be positive.");
                }
            }

            public static void ThrowIfZero(long value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
            {
                if (value == 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, "Must not be zero.");
                }
            }

            public static void ThrowIfLessThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) < 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"Must not be less than {other}.");
                }
            }

            public static void ThrowIfLessThanOrEqual<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) <= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"Must be greater than {other}.");
                }
            }

            public static void ThrowIfGreaterThan<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) > 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"Must not be greater than {other}.");
                }
            }

            public static void ThrowIfGreaterThanOrEqual<T>(T value, T other, [CallerArgumentExpression(nameof(value))] string? paramName = null)
                where T : IComparable<T>
            {
                if (value.CompareTo(other) >= 0)
                {
                    throw new ArgumentOutOfRangeException(paramName, value, $"Must be less than {other}.");
                }
            }
        }

        extension(ArgumentException)
        {
            public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                ArgumentNullException.ThrowIfNull(argument, paramName);
                if (argument.Length == 0)
                {
                    throw new ArgumentException("The value cannot be an empty string.", paramName);
                }
            }

            public static void ThrowIfNullOrWhiteSpace([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
            {
                ArgumentNullException.ThrowIfNull(argument, paramName);
                if (string.IsNullOrWhiteSpace(argument))
                {
                    throw new ArgumentException("The value cannot be an empty string or white space.", paramName);
                }
            }
        }

        extension(ObjectDisposedException)
        {
            public static void ThrowIf([DoesNotReturnIf(true)] bool condition, object instance)
            {
                if (condition)
                {
                    throw new ObjectDisposedException(instance?.GetType().FullName);
                }
            }

            public static void ThrowIf([DoesNotReturnIf(true)] bool condition, Type type)
            {
                if (condition)
                {
                    throw new ObjectDisposedException(type?.FullName);
                }
            }
        }

        extension(Convert)
        {
            public static string ToHexStringLower(byte[] inArray) => Convert.ToHexString(inArray).ToLowerInvariant();

            public static string ToHexStringLower(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

            public static string ToHexStringLower(byte[] inArray, int offset, int length) => Convert.ToHexString(inArray, offset, length).ToLowerInvariant();
        }

        extension(char)
        {
            public static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

            public static bool IsAsciiLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

            public static bool IsAsciiLetterOrDigit(char c) => char.IsAsciiLetter(c) || char.IsAsciiDigit(c);

            public static bool IsAsciiLetterUpper(char c) => c is >= 'A' and <= 'Z';

            public static bool IsAsciiLetterLower(char c) => c is >= 'a' and <= 'z';

            public static bool IsAsciiHexDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
        }

        extension(SHA256)
        {
            public static byte[] HashData(Stream source)
            {
                using var sha = SHA256.Create();
                return sha.ComputeHash(source);
            }

            public static async ValueTask<byte[]> HashDataAsync(Stream source, CancellationToken cancellationToken = default)
            {
                using var sha = SHA256.Create();
                return await sha.ComputeHashAsync(source, cancellationToken).ConfigureAwait(false);
            }
        }

        extension(PemEncoding)
        {
            public static string WriteString(ReadOnlySpan<char> label, ReadOnlySpan<byte> data) => new(PemEncoding.Write(label, data));
        }

        extension<T>(IEnumerable<T> source)
        {
            public IOrderedEnumerable<T> Order() => source.OrderBy(static x => x);

            public IOrderedEnumerable<T> Order(IComparer<T>? comparer) => source.OrderBy(static x => x, comparer);

            public IOrderedEnumerable<T> OrderDescending() => source.OrderByDescending(static x => x);
        }

        extension<T>(ArrayBufferWriter<T> writer)
        {
            public void ResetWrittenCount() => writer.Clear();
        }

        extension(Stream stream)
        {
            public void ReadExactly(Span<byte> buffer)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = stream.Read(buffer[read..]);
                    if (n == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    read += n;
                }
            }

            public void ReadExactly(byte[] buffer, int offset, int count) => stream.ReadExactly(buffer.AsSpan(offset, count));

            public async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int read = 0;
                while (read < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    read += n;
                }
            }

            public ValueTask ReadExactlyAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) =>
                stream.ReadExactlyAsync(buffer.AsMemory(offset, count), cancellationToken);

            public async ValueTask<int> ReadAtLeastAsync(Memory<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true, CancellationToken cancellationToken = default)
            {
                int read = 0;
                while (read < minimumBytes)
                {
                    int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
                    if (n == 0)
                    {
                        if (throwOnEndOfStream)
                        {
                            throw new EndOfStreamException();
                        }

                        break;
                    }

                    read += n;
                }

                return read;
            }
        }

        extension(CancellationTokenSource source)
        {
            public Task CancelAsync()
            {
                source.Cancel();
                return Task.CompletedTask;
            }
        }

        extension(Process process)
        {
            public bool WaitForExit(TimeSpan timeout) => process.WaitForExit(ToMilliseconds(timeout));
        }

        extension(Task task)
        {
            public bool Wait(TimeSpan timeout, CancellationToken cancellationToken) => task.Wait(ToMilliseconds(timeout), cancellationToken);

            /// <summary><c>ConfigureAwait(ConfigureAwaitOptions)</c> of .NET 8; ForceYielding is not needed by this code.</summary>
            public ConfiguredTaskAwaitable ConfigureAwait(ConfigureAwaitOptions options)
            {
                bool context = (options & ConfigureAwaitOptions.ContinueOnCapturedContext) != 0;
                if ((options & ConfigureAwaitOptions.SuppressThrowing) == 0)
                {
                    return task.ConfigureAwait(context);
                }

                // Observes the exception, so it neither throws here nor reaches TaskScheduler.UnobservedTaskException.
                return task.ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).ConfigureAwait(context);
            }
        }

        extension<T>(Span<T> span)
            where T : IEquatable<T>
        {
            public int IndexOfAnyExcept(T value) => ((ReadOnlySpan<T>)span).IndexOfAnyExcept(value);

            public int IndexOfAnyExcept(ReadOnlySpan<T> values) => ((ReadOnlySpan<T>)span).IndexOfAnyExcept(values);
        }

        extension<T>(ReadOnlySpan<T> span)
            where T : IEquatable<T>
        {
            public int IndexOfAnyExcept(T value)
            {
                for (int i = 0; i < span.Length; i++)
                {
                    if (!span[i].Equals(value))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public int IndexOfAnyExcept(ReadOnlySpan<T> values)
            {
                for (int i = 0; i < span.Length; i++)
                {
                    if (values.IndexOf(span[i]) < 0)
                    {
                        return i;
                    }
                }

                return -1;
            }

            public int IndexOfAnyExcept(SearchValues<T> values) => span.IndexOfAnyExcept(values.Values);

            public int IndexOfAny(SearchValues<T> values) => span.IndexOfAny(values.Values);
        }

        private static int ToMilliseconds(TimeSpan timeout) =>
            timeout == Timeout.InfiniteTimeSpan ? Timeout.Infinite : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
    }
}
