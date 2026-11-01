namespace Workbench.Protocol;

/// <summary>Nonsecret identity pinned by a desktop connection; authentication never leaves the endpoint.</summary>
public sealed record RemoteServiceIdentity(string ServiceId, string InstanceId, int ProtocolVersion,
    string Platform, int ProcessId, DateTimeOffset StartedAt);

/// <summary>An endpoint-local, owner-readable bootstrap file. Never copy this record to desktop settings.</summary>
public sealed record RemoteServiceEndpoint(RemoteServiceIdentity Identity, string Address, string Token)
{
    public override string ToString() => $"RemoteServiceEndpoint {{ Identity = {Identity}, Address = {Address}, Token = [redacted] }}";
}
