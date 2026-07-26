using System;

namespace Foundry.Core.Attributes
{
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class PartitionedAttribute : Attribute
    {
        private readonly int _archiveThresholdYears;

        public PartitionedAttribute(int archiveThresholdYears = 2)
        {
            _archiveThresholdYears = archiveThresholdYears;
        }

        public int ArchiveThresholdYears => _archiveThresholdYears;
    }
}
