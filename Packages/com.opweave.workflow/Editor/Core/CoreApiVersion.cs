#nullable enable

using System;

namespace OpWeave.Core
{
    /// <summary>
    /// A Core public API version of SPEC §B0.2: exactly
    /// <c>MAJOR.MINOR.PATCH</c>, three non-negative integers without leading
    /// zeros, prerelease or build metadata. Ordering is numeric Major → Minor →
    /// Patch and is independent of the <c>com.opweave.workflow</c> package SemVer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPEC §B0.2 declares this as <c>readonly record struct</c>; it is written as
    /// an explicit <c>readonly struct</c> because the project toolchain targets C# 9
    /// (the dotnet build pins <c>LangVersion</c> 9 to match the Unity compiler),
    /// which predates <c>record struct</c>. The members below reproduce the
    /// positional record-struct surface (constructor, <c>Major</c>/<c>Minor</c>/
    /// <c>Patch</c>, <c>Deconstruct</c>, value equality, <c>==</c>/<c>!=</c>,
    /// record-style <c>ToString</c>) plus the declared
    /// <see cref="IComparable{T}"/> ordering and <see cref="Parse"/>.
    /// </para>
    /// <para>
    /// A malformed input fails through <see cref="Result{T}"/> carrying one
    /// internal diagnostic (empty code, fixed English, never an exception for
    /// format errors — the same convention as the SG-02 reader); the owning layer
    /// maps it to a published code — W2.12 emits OW-OP-012 (SPEC §B0.2 rules 1, 3).
    /// </para>
    /// </remarks>
    public readonly struct CoreApiVersion : IComparable<CoreApiVersion>, IEquatable<CoreApiVersion>
    {
        /// <summary>Creates the version <c>Major.Minor.Patch</c>.</summary>
        public CoreApiVersion(int Major, int Minor, int Patch)
        {
            this.Major = Major;
            this.Minor = Minor;
            this.Patch = Patch;
        }

        public int Major { get; }

        public int Minor { get; }

        public int Patch { get; }

        /// <summary>
        /// Parses a strict <c>MAJOR.MINOR.PATCH</c> version (SPEC §B0.2): three
        /// dot-separated non-negative integers; leading zeros, signs, whitespace,
        /// prerelease (<c>-beta</c>) and build metadata (<c>+abc</c>) are rejected,
        /// as is any component outside the <see cref="int"/> range.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        public static Result<CoreApiVersion> Parse(string text)
        {
            if (text is null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var numbers = new int[3];
            var start = 0;
            for (var index = 0; index < numbers.Length; index++)
            {
                // The last component runs to the end; a further '.' inside it is a
                // non-digit and fails TryComponent, so "1.0.0.0" is rejected here.
                var end = index < numbers.Length - 1
                    ? text.IndexOf('.', start)
                    : text.Length;
                if (end < 0 || !TryComponent(text, start, end, out numbers[index]))
                {
                    return Malformed(text);
                }
                start = end + 1;
            }
            return Result<CoreApiVersion>.Success(
                new CoreApiVersion(numbers[0], numbers[1], numbers[2]));
        }

        /// <summary>Numeric ordering Major → Minor → Patch (SPEC §B0.2 rule 2).</summary>
        public int CompareTo(CoreApiVersion other)
        {
            var major = Major.CompareTo(other.Major);
            if (major != 0)
            {
                return major;
            }
            var minor = Minor.CompareTo(other.Minor);
            if (minor != 0)
            {
                return minor;
            }
            return Patch.CompareTo(other.Patch);
        }

        public void Deconstruct(out int Major, out int Minor, out int Patch)
        {
            Major = this.Major;
            Minor = this.Minor;
            Patch = this.Patch;
        }

        public bool Equals(CoreApiVersion other) =>
            Major == other.Major && Minor == other.Minor && Patch == other.Patch;

        public override bool Equals(object? obj) =>
            obj is CoreApiVersion other && Equals(other);

        public override int GetHashCode() => (Major, Minor, Patch).GetHashCode();

        public static bool operator ==(CoreApiVersion left, CoreApiVersion right) =>
            left.Equals(right);

        public static bool operator !=(CoreApiVersion left, CoreApiVersion right) =>
            !left.Equals(right);

        public override string ToString() =>
            $"{nameof(CoreApiVersion)} {{ Major = {Major}, Minor = {Minor}, Patch = {Patch} }}";

        // Scans one component [start, end): "0" or a multi-digit run not starting
        // with '0' (no leading zeros), all ASCII digits, at most int.MaxValue.
        private static bool TryComponent(string text, int start, int end, out int value)
        {
            value = 0;
            var length = end - start;
            if (length == 0 || (length > 1 && text[start] == '0'))
            {
                return false;
            }
            var number = 0L;
            for (var i = start; i < end; i++)
            {
                var c = text[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
                number = number * 10 + (c - '0');
                if (number > int.MaxValue)
                {
                    return false;
                }
            }
            value = (int)number;
            return true;
        }

        // Internal diagnostic convention (empty code, fixed English): the caller
        // maps a parse failure to a published code — W2.12 emits OW-OP-012.
        private static Result<CoreApiVersion> Malformed(string text) =>
            Result<CoreApiVersion>.Failure(new Diagnostic(
                string.Empty,
                Severity.Error,
                $"Malformed CoreApi version '{text}'.",
                Hint: "Expected MAJOR.MINOR.PATCH: three non-negative integers without " +
                    "leading zeros, prerelease or build metadata (SPEC §B0.2)."));
    }
}
