using System;

namespace Foundry.Core.Attributes;

/// <summary>
/// When applied to an entity, all read operations (GetById, FindMany, etc.) 
/// on that entity will be recorded in the audit log.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class ReadAuditedAttribute : Attribute
{
}
