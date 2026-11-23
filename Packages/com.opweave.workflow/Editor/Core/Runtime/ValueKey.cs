#nullable enable

using System;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The key under which the ValueStore holds one step output (SPEC §E5):
    /// <c>ValueKey(Frame, Iteration, StepIndex, Port)</c>. <see cref="Frame"/>
    /// and <see cref="Iteration"/> are reserved for sub-workflows and ForEach
    /// (SPEC §N.1) and are always 0 in v1, so a key is effectively the
    /// (step index, output port) pair of a <c>StepSucceeded</c> record.
    /// </summary>
    /// <remarks>
    /// SPEC §E5 declares this as <c>readonly record struct</c>; it is written
    /// as an explicit <c>readonly struct</c> because the project toolchain
    /// targets C# 9 (the dotnet build pins <c>LangVersion</c> 9 to match the
    /// Unity compiler), which predates <c>record struct</c>. The members below
    /// reproduce the positional record-struct surface (constructor, the four
    /// properties, <c>Deconstruct</c>, value equality, <c>==</c>/<c>!=</c>,
    /// record-style <c>ToString</c>) — the same convention
    /// <see cref="CoreApiVersion"/> established.
    /// </remarks>
    public readonly struct ValueKey : IEquatable<ValueKey>
    {
        /// <summary>Creates the key <c>(Frame, Iteration, StepIndex, Port)</c>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="Port"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="Frame"/>, <paramref name="Iteration"/> or
        /// <paramref name="StepIndex"/> is negative.
        /// </exception>
        public ValueKey(int Frame, int Iteration, int StepIndex, string Port)
        {
            if (Frame < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Frame), Frame, "Frame must not be negative.");
            }
            if (Iteration < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Iteration), Iteration, "Iteration must not be negative.");
            }
            if (StepIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(StepIndex), StepIndex, "StepIndex must not be negative.");
            }
            this.Frame = Frame;
            this.Iteration = Iteration;
            this.StepIndex = StepIndex;
            this.Port = Port ?? throw new ArgumentNullException(nameof(Port));
        }

        /// <summary>The sub-workflow frame (SPEC §N.1 reservation; always 0 in v1).</summary>
        public int Frame { get; }

        /// <summary>The ForEach iteration (SPEC §N.1 reservation; always 0 in v1).</summary>
        public int Iteration { get; }

        /// <summary>The step's index in the Plan's D5 execution order (SPEC §D7).</summary>
        public int StepIndex { get; }

        /// <summary>The step's output port name.</summary>
        public string Port { get; }

        public void Deconstruct(out int Frame, out int Iteration, out int StepIndex, out string Port)
        {
            Frame = this.Frame;
            Iteration = this.Iteration;
            StepIndex = this.StepIndex;
            Port = this.Port;
        }

        public bool Equals(ValueKey other) =>
            Frame == other.Frame && Iteration == other.Iteration &&
            StepIndex == other.StepIndex &&
            string.Equals(Port, other.Port, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is ValueKey other && Equals(other);

        public override int GetHashCode() => (Frame, Iteration, StepIndex, Port).GetHashCode();

        public static bool operator ==(ValueKey left, ValueKey right) => left.Equals(right);

        public static bool operator !=(ValueKey left, ValueKey right) => !left.Equals(right);

        public override string ToString() =>
            $"{nameof(ValueKey)} {{ Frame = {Frame}, Iteration = {Iteration}, " +
            $"StepIndex = {StepIndex}, Port = {Port} }}";
    }
}
