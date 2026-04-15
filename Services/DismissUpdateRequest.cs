namespace HirschNotify.Services;

/// <summary>
/// Body shape for <c>POST /updates/dismiss</c>. The layout banner's inline
/// fetch() sends the version being dismissed so the server can persist it
/// under <c>Updates:LastDismissedVersion</c>.
/// </summary>
public sealed record DismissUpdateRequest(string Version);
