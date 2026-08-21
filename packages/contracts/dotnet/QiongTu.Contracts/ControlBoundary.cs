namespace QiongTu.Contracts;

public sealed record ControlBoundary(
    string ApiVersion,
    string EndpointKind,
    string EndpointName,
    bool LanBindingAllowed);

public sealed record ControlSelfTestResult(
    string ApiVersion,
    string Status,
    ControlBoundary Boundary,
    IReadOnlyList<string> Checks);
