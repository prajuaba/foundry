using System;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Foundry.Core.Audit;

namespace Foundry.RealTime.Pipeline;

/// <summary>
/// A decorator audit sink that intercepts write logs and forwards them to the real-time broker.
/// </summary>
public class RealTimeAuditSink : IAuditSink
{
    private readonly IRealTimeNotificationBroker _broker;
    private readonly IAuditSink? _innerSink;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Foundry.Core.Attributes.RealTimeAttribute?> _attributeCache = new();

    public RealTimeAuditSink(IRealTimeNotificationBroker broker, IAuditSink? innerSink = null)
    {
        _broker = broker;
        _innerSink = innerSink;
    }

    private bool IsRealTimeEnabled(string entityTypeName)
    {
        var rtAttr = _attributeCache.GetOrAdd(entityTypeName, typeName =>
        {
            var type = Type.GetType(typeName);
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(typeName);
                    if (type != null) break;
                }
            }
            return type?.GetCustomAttribute<Foundry.Core.Attributes.RealTimeAttribute>();
        });

        // Requires the declaration. This read `rtAttr == null || rtAttr.Enabled`, so an entity that
        // said nothing was broadcast -- including framework types nobody declares at all: every
        // subscriber received OutboxMessage mutations, and received them regardless of role, since
        // an undeclared type also carries no realTimeRoles to check against.
        //
        // An author who omits the flag is not asking for the feature.
        return rtAttr is { Enabled: true };
    }

    public async Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        if (_innerSink != null)
        {
            await _innerSink.WriteAsync(entry, ct);
        }

        if (IsRealTimeEnabled(entry.EntityType))
        {
            await _broker.BroadcastMutationAsync(entry, ct);
        }
    }

    public async Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default)
    {
        if (_innerSink != null)
        {
            await _innerSink.WriteManyAsync(entries, ct);
        }
        foreach (var entry in entries)
        {
            if (IsRealTimeEnabled(entry.EntityType))
            {
                await _broker.BroadcastMutationAsync(entry, ct);
            }
        }
    }
}
