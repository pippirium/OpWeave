#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// A <see cref="IValueCodec"/> that also round-trips values to a CLR type
    /// <typeparamref name="T"/> (SPEC §B7, §C0.1); implemented by OpWeave.UnityAdapter
    /// and external extensions for C#-backed types.
    /// </summary>
    /// <remarks>
    /// Per SPEC §B7.1 item 2, <see cref="IValueCodec.Normalize"/> on a CLR codec
    /// must produce Canonical JSON byte-identical to
    /// <c>ToJson(FromJson(value))</c>.
    /// </remarks>
    public interface IClrValueCodec<T> : IValueCodec
    {
        /// <summary>Converts a validated <see cref="JsonValue"/> to the CLR value.</summary>
        T FromJson(JsonValue value);

        /// <summary>Converts the CLR value back to its canonical <see cref="JsonValue"/>.</summary>
        JsonValue ToJson(T value);
    }
}
