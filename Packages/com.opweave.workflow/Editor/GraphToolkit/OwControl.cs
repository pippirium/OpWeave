#nullable enable

using System;

namespace OpWeave.GraphToolkit
{
    /// <summary>
    /// The C# data type of every control port (SPEC §H6.3): the single control
    /// input of an <see cref="OperationNode"/> and each named
    /// <c>ControlOutputs</c> output (<c>"next"</c> in v1, SPEC appendix N).
    /// A marker type — control ports carry execution order, never a value.
    /// </summary>
    [Serializable]
    public sealed class OwControl
    {
    }
}
