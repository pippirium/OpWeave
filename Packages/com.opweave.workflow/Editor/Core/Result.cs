#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// Layer-boundary result (SPEC §0.4): failures are reported as <see cref="Diagnostic"/>s,
    /// never as exceptions. <see cref="Diagnostics"/> is never null; a failure carries at
    /// least one diagnostic while a success may still carry warnings.
    /// </summary>
    public readonly struct Result<T>
    {
        private readonly T? _value;
        private readonly IReadOnlyList<Diagnostic>? _diagnostics;

        // Shared immutable empty list; also covers default(Result<T>).Diagnostics.
        private static readonly IReadOnlyList<Diagnostic> s_emptyDiagnostics =
            Array.AsReadOnly(Array.Empty<Diagnostic>());

        private Result(bool ok, T? value, IReadOnlyList<Diagnostic> diagnostics)
        {
            Ok = ok;
            _value = value;
            _diagnostics = diagnostics;
        }

        public bool Ok { get; }

        /// <summary>The produced value on success; <c>default</c> on failure.</summary>
        public T? Value => _value;

        /// <summary>
        /// Diagnostics emitted while producing this result; empty when there are none.
        /// The list is an immutable defensive copy (SPEC §0.4) and cannot be cast back to
        /// the source array or mutated through collection interfaces.
        /// </summary>
        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics ?? s_emptyDiagnostics;

        public static Result<T> Success(T value) =>
            new Result<T>(true, value, s_emptyDiagnostics);

        public static Result<T> Success(T value, params Diagnostic[] diagnostics) =>
            Success(value, (IEnumerable<Diagnostic>)diagnostics);

        public static Result<T> Success(T value, IEnumerable<Diagnostic> diagnostics)
        {
            if (diagnostics is null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }
            return new Result<T>(true, value, ReadOnlyCollections.CopyList(diagnostics));
        }

        public static Result<T> Failure(params Diagnostic[] diagnostics) =>
            Failure((IEnumerable<Diagnostic>)diagnostics);

        public static Result<T> Failure(IEnumerable<Diagnostic> diagnostics)
        {
            if (diagnostics is null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }
            var copy = ReadOnlyCollections.CopyList(diagnostics);
            if (copy.Count == 0)
            {
                throw new ArgumentException(
                    "A failed result must carry at least one diagnostic.", nameof(diagnostics));
            }
            return new Result<T>(false, default, copy);
        }
    }
}
