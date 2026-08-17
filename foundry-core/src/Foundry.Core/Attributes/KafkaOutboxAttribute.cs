using System;

namespace Foundry.Core.Attributes;

/// <summary>
/// Marks an entity whose mutations are published to the transactional outbox.
/// </summary>
/// <remarks>
/// <para>
/// The IR's <c>enableKafkaOutbox</c> had no runtime form. It selected which consumers the compiler
/// registered and nothing else, so the outbox had no way to tell an entity that had opted in from
/// one that had not -- and published every mutation of every entity, deriving a topic name from the
/// type when none was declared.
/// </para>
/// <para>
/// Observed in an application whose schema enabled the outbox for four entities out of twenty-four:
/// 1,013 of 1,024 outbox rows belonged to entities that had never asked for it, and creating a
/// record of one of them produced a topic named after it carrying the entity's full body.
/// </para>
/// <para>
/// This is deliberately separate from <see cref="KafkaTopicAttribute"/>. An entity may opt in
/// without naming a topic, in which case the dispatcher derives one; the two questions are "should
/// this be published at all" and "where to", and conflating them is what left the first unanswerable.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class KafkaOutboxAttribute : Attribute
{
}
