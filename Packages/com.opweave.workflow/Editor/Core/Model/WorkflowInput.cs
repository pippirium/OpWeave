#nullable enable

namespace OpWeave.Core.Model
{
    /// <summary>
    /// A workflow input <c>{id, type, required, default?, description?}</c>: an argument
    /// injected from outside (SPEC §A6). <see cref="Default"/> is the value used when a
    /// Run supplies no input value; it does not change the supply-source count of the
    /// bound ports (SPEC §A6).
    /// </summary>
    public sealed record WorkflowInput(
        string Id,
        string Type,
        bool Required,
        JsonValue? Default = null,
        string? Description = null);
}
