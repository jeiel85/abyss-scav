using AbyssScav.Protocol;
using Godot;

namespace AbyssScav.Net;

/// <summary>
/// UPnP helper for internet direct connection (docs/02 §2D, docs/16 §3).
/// Discovery runs on a worker thread so the main loop never blocks; every
/// failure is nonfatal and maps to manual-setup UI copy. Only mappings owned
/// by this instance are ever removed.
/// </summary>
public sealed class UpnpPortMappingService : IPortMappingService, IDisposable
{
    private const int DiscoverTimeoutMs = 2000;
    private const int DiscoverTtl = 2;
    private const string DeviceFilter = "InternetGatewayDevice";
    private const string Description = "AbyssScav";
    private const string UdpProto = "UDP";

    private Upnp? _gateway;
    private int _ownedPort = -1;
    private bool _disposed;

    public async Task<PortMappingResult> TryMapUdpAsync(int port, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!SessionValidation.IsValidPort(port))
        {
            return new PortMappingResult(
                PortMappingStatus.MappingFailed, string.Empty,
                $"[{NetErrors.PortMappingUnavailable}] Port {port} is out of range. " + PortMappingCopy.ManualRequired);
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            return await Task.Run(() => DiscoverAndMap(port, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PortMappingResult(
                PortMappingStatus.GatewayNotFound, string.Empty,
                $"[{NetErrors.PortMappingUnavailable}] Automatic port mapping is unavailable ({ex.Message}). " +
                PortMappingCopy.ManualRequired);
        }
    }

    public async Task RemoveMappingAsync(CancellationToken ct)
    {
        var gateway = _gateway;
        var port = _ownedPort;
        _gateway = null;
        _ownedPort = -1;
        if (gateway is null || port <= 0)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    gateway.DeletePortMapping(port, UdpProto);
                }
                catch
                {
                    // Cleanup is best-effort and never throws.
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation still releases ownership; the lease expires on its own.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var gateway = _gateway;
        var port = _ownedPort;
        _gateway = null;
        _ownedPort = -1;
        if (gateway is null || port <= 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                gateway.DeletePortMapping(port, UdpProto);
            }
            catch
            {
                // Best-effort cleanup off the main thread.
            }
        });
    }

    private PortMappingResult DiscoverAndMap(int port, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var gateway = new Upnp();
        int found;
        try
        {
            found = gateway.Discover(DiscoverTimeoutMs, DiscoverTtl, DeviceFilter);
        }
        catch (Exception ex)
        {
            return new PortMappingResult(
                PortMappingStatus.GatewayNotFound, string.Empty,
                $"[{NetErrors.PortMappingUnavailable}] No UPnP gateway answered ({ex.Message}). " +
                PortMappingCopy.ManualRequired + " " + PortMappingCopy.CgnatNote);
        }

        ct.ThrowIfCancellationRequested();
        if (found <= 0)
        {
            return new PortMappingResult(
                PortMappingStatus.GatewayNotFound, string.Empty,
                $"[{NetErrors.PortMappingUnavailable}] No UPnP gateway found. " +
                PortMappingCopy.ManualRequired + " " + PortMappingCopy.CgnatNote);
        }

        var added = gateway.AddPortMapping(port, port, Description, UdpProto);
        if ((int)added != 0)
        {
            return new PortMappingResult(
                PortMappingStatus.MappingFailed, string.Empty,
                $"[{NetErrors.PortMappingUnavailable}] Gateway refused UDP {port} ({added}). " +
                PortMappingCopy.ManualRequired);
        }

        string external;
        try
        {
            external = gateway.QueryExternalAddress();
        }
        catch
        {
            external = string.Empty;
        }

        _gateway = gateway;
        _ownedPort = port;
        return new PortMappingResult(PortMappingStatus.Mapped, external ?? string.Empty, PortMappingCopy.Ready);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(UpnpPortMappingService));
        }
    }
}
