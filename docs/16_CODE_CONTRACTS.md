# 16. Code Contracts & Core Interfaces — GitHub Track

## 1. Network Transport
```csharp
public interface INetworkTransport
{
    bool IsHost { get; }
    ulong LocalPeerId { get; }
    event Action<PeerConnectedEvent> PeerConnected;
    event Action<PeerDisconnectedEvent> PeerDisconnected;
    event Action<NetMessage> MessageReceived;

    Task HostAsync(SessionOptions options, CancellationToken ct);
    Task JoinAsync(SessionAddress address, CancellationToken ct);
    void SendReliable(ulong peerId, ReadOnlySpan<byte> payload);
    void SendUnreliable(ulong peerId, ReadOnlySpan<byte> payload);
    void Poll();
    void Shutdown();
}
```
기본 구현은 `EnetTransport`다.

## 2. LAN Discovery
```csharp
public interface ILanDiscoveryService
{
    void StartAdvertise(LanSessionAdvertisement info);
    void StopAdvertise();
    void StartScan();
    IReadOnlyList<DiscoveredLanSession> Snapshot();
    void StopScan();
}
```
Discovery packet은 gameplay port와 별도 discovery port를 사용할 수 있으며 최대 크기를 제한한다.

## 3. UPnP
```csharp
public interface IPortMappingService
{
    Task<PortMappingResult> TryMapUdpAsync(int port, CancellationToken ct);
    Task RemoveMappingAsync(CancellationToken ct);
}
```
실패는 fatal이 아니다. UI가 수동 설정 안내로 전환한다.

## 4. Authority
```csharp
public interface IAuthoritativeCommand<TCommand, TResult>
{
    bool Validate(in TCommand command, in CommandContext context, out string reason);
    TResult Execute(in TCommand command, in CommandContext context);
}
```
Client는 최종 state가 아니라 intent를 보낸다.

## 5. Run Manifest
```csharp
public sealed record RunManifest(
    ushort ProtocolVersion,
    string GameVersion,
    ulong RunSeed,
    string BiomeId,
    string ContractId,
    string DifficultyId,
    string LayoutHash,
    string CatalogHash,
    IReadOnlyList<string> Modifiers);
```

## 6. Sonar
```csharp
public interface ISonarService
{
    SonarPulseResult EmitActivePulse(SonarPulseRequest request);
    IReadOnlyList<SonarContact> QueryPassiveContacts(Vector3 origin, float dt);
}
```
Gameplay contact는 host 계산, visual shader는 client-local.

## 7. Generation
```csharp
public interface IRunGenerator
{
    GeneratedRun Generate(in RunGenerationRequest request);
    GenerationValidationResult Validate(in GeneratedRun run);
}
```
validation 통과 전 `InRun` 전환 금지.

## 8. Save
```csharp
public interface ISaveStore
{
    Task<ProfileSave> LoadAsync(CancellationToken ct);
    Task SaveAsync(ProfileSave save, CancellationToken ct);
    Task<RestoreResult> RestoreBackupAsync(int generation, CancellationToken ct);
}
```

## 9. Settlement idempotency
```text
if profile.applied_settlement_ids contains settlement.id:
    return AlreadyApplied
apply rewards
append settlement.id
atomic_save(profile)
```

## 10. Stable IDs
- lower snake/dot namespace
- stable 공개 빌드 이후 rename 금지
- rename 필요 시 migration alias
예: `module.sonar.whisper_pulse`

## 11. Error domains
- `BOOT-*`
- `SAVE-*`
- `NET-*`
- `GEN-*`
- `CONTENT-*`
- `BUILD-*`

오류는 `code + localization key + diagnostic context`로 구성한다.

## 12. Threading
- Godot SceneTree mutation: main thread only
- file IO: async worker 가능
- generation preprocessing: worker 가능, Node 직접 수정 금지
- network events: main-loop dispatch queue를 거쳐 domain으로 전달
