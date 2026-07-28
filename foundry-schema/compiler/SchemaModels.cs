using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Foundry.Schema.Compiler
{
    public record SchemaModel
    {
        public string Namespace { get; init; } = string.Empty;
        public string Version { get; init; } = "1.0.0";
        public List<Entity> Entities { get; init; } = new();
        public List<Enum> Enums { get; init; } = new();
        public List<DtoModel> Dtos { get; init; } = new();
        public List<CustomEndpoint> CustomEndpoints { get; init; } = new();
        public List<WorkflowModel> Workflows { get; init; } = new();
        public List<ConnectorModel> Connectors { get; init; } = new();
    }

    public record ConnectorModel
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = "REST"; // REST, SOAP, GraphQL
        public string BaseUrl { get; init; } = string.Empty;
        public string AuthType { get; init; } = "None"; // None, Basic, ApiKey, Bearer, OAuth2
        public string? Username { get; init; }
        public string? Password { get; init; }
        public string? ApiKey { get; init; }
        public string? ApiKeyHeaderName { get; init; } = "X-API-Key";
        public string? Token { get; init; }
        public string? SoapAction { get; init; }
        public int TimeoutSeconds { get; init; } = 30;
        public int MaxRetries { get; init; } = 3;
    }

    public record WorkflowModel
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Entity { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string EffectiveDate { get; init; } = string.Empty;
        public string ExpirationDate { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public List<WorkflowStateModel> States { get; init; } = new();
        public List<WorkflowTransitionModel> Transitions { get; init; } = new();
        public List<WorkflowChoiceNodeModel> ChoiceNodes { get; init; } = new();
    }

    public record WorkflowChoiceNodeModel
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public List<WorkflowBranchModel> Branches { get; init; } = new();
    }

    public record WorkflowBranchModel
    {
        public string Id { get; init; } = string.Empty;
        public WorkflowConditionModel Condition { get; init; } = new();
        public string TargetState { get; init; } = string.Empty;
    }

    public record WorkflowStateModel
    {
        public string Name { get; init; } = string.Empty;
        public bool IsInitial { get; init; }
        public bool IsFinal { get; init; }
        public List<string> AllowedRoles { get; init; } = new();
    }

    public record WorkflowTransitionModel
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FromState { get; init; } = string.Empty;
        public string ToState { get; init; } = string.Empty;
        public string Trigger { get; init; } = string.Empty;
        public bool UseCustomCommand { get; init; }
        public List<string> RequiredRoles { get; init; } = new();
        public List<WorkflowConditionModel> Conditions { get; init; } = new();
        public List<WorkflowActionModel> Actions { get; init; } = new();
    }

    public record WorkflowConditionModel
    {
        public string Type { get; init; } = string.Empty;
        public string Property { get; init; } = string.Empty;
        public string Operator { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    public record WorkflowActionModel
    {
        public string Type { get; init; } = string.Empty;
        
        // Internal API
        public string? RequestType { get; init; }
        public string? PayloadTemplate { get; init; }
        
        // External API
        public string? Method { get; init; }
        public string? Url { get; init; }
        public Dictionary<string, string>? Headers { get; init; }
        public string? BodyTemplate { get; init; }
    }

    public record Entity
    {
        public string Name { get; init; } = string.Empty;
        public string? BaseClass { get; init; }
        public bool SoftDelete { get; init; }
        public bool Auditable { get; init; }
        public bool Partitioned { get; init; }
        public int ArchiveThresholdYears { get; init; } = 2;

        [JsonPropertyName("enableRealTime")]
        public bool RealTime { get; init; }

        [JsonPropertyName("realTimeRoles")]
        public List<string> RealTimeRoles { get; init; } = new();

        [JsonPropertyName("enableKafkaOutbox")]
        public bool KafkaOutboxEnabled { get; init; }

        [JsonPropertyName("kafkaTopic")]
        public string? KafkaTopic { get; init; }

        [JsonPropertyName("enableFileIO")]
        public bool FileIoEnabled { get; init; }

        [JsonPropertyName("fileIOAllowedExtensions")]
        public List<string> FileIoAllowedExtensions { get; init; } = new();

        [JsonPropertyName("enableGraphQL")]
        public bool GraphQlEnabled { get; init; }

        [JsonPropertyName("multiTenant")]
        public bool MultiTenant { get; init; }

        [JsonPropertyName("tenantProperty")]
        public string? TenantProperty { get; init; }

        /// <summary>
        /// Restricts rows to the caller who created them, in addition to any tenant filter.
        /// </summary>
        /// <remarks>
        /// Roles answer "may this caller call this endpoint"; ownership answers "which rows may they
        /// see through it". Without it, any caller holding a declared role reaches every row in their
        /// tenant — adequate for back-office tools, not for anything where users hold their own records.
        /// </remarks>
        [JsonPropertyName("ownerScoped")]
        public bool OwnerScoped { get; init; }

        /// <summary>
        /// Roles that see every row in the tenant rather than only their own.
        /// </summary>
        /// <remarks>
        /// Exemption lifts the owner filter and never the tenant filter, so it widens access within
        /// one tenant and cannot cross tenants.
        /// </remarks>
        [JsonPropertyName("ownerExemptRoles")]
        public List<string> OwnerExemptRoles { get; init; } = new();

        /// <summary>
        /// Roles that see every row in the tenant but may not change any of them.
        /// </summary>
        /// <remarks>
        /// <see cref="OwnerExemptRoles"/> is per entity rather than per operation, so a role exempted
        /// for reads was exempted for updates and deletes too. Read-only oversight — an auditor, a
        /// compliance reviewer, a support agent who may look but not touch — could not be expressed
        /// at all.
        /// </remarks>
        [JsonPropertyName("ownerReadExemptRoles")]
        public List<string> OwnerReadExemptRoles { get; init; } = new();

        public Dictionary<string, List<string>> ApiBusinessRules { get; init; } = new();

        /// <summary>
        /// HTTP methods to expose for this entity's generated CRUD surface,
        /// e.g. <c>GET</c>, <c>POST</c>, <c>GET_BY_ID</c>, <c>PUT</c>, <c>DELETE</c>.
        /// </summary>
        /// <remarks>
        /// Studio has always written this, along with <see cref="ApiRoles"/> and
        /// <see cref="ApiCaching"/>, but the compiler had no matching properties — so
        /// deserialisation silently discarded them and the settings a user configured in the
        /// visual designer had no effect anywhere, with no warning.
        /// </remarks>
        public List<string> ApiEnabledMethods { get; init; } = new();

        /// <summary>Roles permitted per HTTP method.</summary>
        public Dictionary<string, List<string>> ApiRoles { get; init; } = new();

        /// <summary>Response caching configuration per HTTP method.</summary>
        public Dictionary<string, ApiCachingConfig> ApiCaching { get; init; } = new();

        public List<Property> Properties { get; init; } = new();
        public List<Index> Indexes { get; init; } = new();
    }

    /// <summary>
    /// Response caching settings for one generated endpoint method.
    /// </summary>
    public record ApiCachingConfig
    {
        /// <summary>Whether responses are cached.</summary>
        public bool Enabled { get; init; }

        /// <summary>Cache lifetime in seconds.</summary>
        public int TtlSeconds { get; init; }
    }

    public record Property
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public bool IsKey { get; init; }
        public bool IsTenantKey { get; init; }

        /// <summary>
        /// Marks the property that records which caller owns the row. Must be named <c>OwnerId</c>.
        /// </summary>
        [JsonPropertyName("isOwnerKey")]
        public bool IsOwnerKey { get; init; }

        /// <summary>
        /// Marks the property holding the identities a row is granted to. Must be named
        /// <c>SharedWith</c> and be a <c>List&lt;string&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Sharing, delegation and team scoping are one predicate seen from three angles, differing
        /// only in what the grant names: a subject id shares with a person, a group id shares with a
        /// team. The caller's identities are their subject plus the groups their token carries.
        /// A grant confers read access only.
        /// </remarks>
        [JsonPropertyName("isSharedWithKey")]
        public bool IsSharedWithKey { get; init; }

        public bool IsEnum { get; init; }
        public List<string> Attributes { get; init; } = new();
    }

    public record Index
    {
        public string Name { get; init; } = string.Empty;
        public List<string> Fields { get; init; } = new();
        public bool Unique { get; init; }

        [JsonPropertyName("isUnique")]
        public bool IsUnique { get => Unique; init => Unique = value; }
    }

    public record Enum
    {
        public string Name { get; init; } = string.Empty;
        public List<string> Values { get; init; } = new();
    }

    public record DtoModel
    {
        public string Name { get; init; } = string.Empty;
        public List<DtoProperty> Properties { get; init; } = new();

        [JsonPropertyName("enableKafkaOutbox")]
        public bool KafkaOutboxEnabled { get; init; }

        [JsonPropertyName("kafkaTopic")]
        public string? KafkaTopic { get; init; }

        [JsonPropertyName("enableFileIO")]
        public bool FileIoEnabled { get; init; }

        [JsonPropertyName("fileIOAllowedExtensions")]
        public List<string> FileIoAllowedExtensions { get; init; } = new();
    }

    public record DtoProperty
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string? SourceEntity { get; init; }
        public string? SourceProperty { get; init; }
        public bool IsRequired { get; init; }
        public List<string> Attributes { get; init; } = new();
    }

    public record CustomEndpoint
    {
        public string Method { get; init; } = "GET";
        public string Route { get; init; } = string.Empty;
        public string RequestType { get; init; } = string.Empty;
        public string TargetEntity { get; init; } = string.Empty;
        public string OperationType { get; init; } = "Query"; // Query, Insert, Update, Custom
        public string? FilterField { get; init; }

        /// <summary>
        /// Comparison used against <see cref="FilterField"/>, e.g. <c>Equals</c>.
        /// </summary>
        /// <remarks>
        /// Another field Studio emitted into a property the compiler did not declare, so it was
        /// dropped on load. The generated handler currently always emits an equality comparison;
        /// honouring other operators is a generator change, but the value now at least survives
        /// the round trip instead of vanishing.
        /// </remarks>
        public string? FilterOperator { get; init; }

        public string? FilterSourceValue { get; init; }
        public List<AssignmentRule>? Assignments { get; init; }
        public List<string>? Roles { get; init; }
        public List<string>? BusinessRules { get; init; }
    }

    public record AssignmentRule
    {
        public string EntityProperty { get; init; } = string.Empty;
        public string SourceValue { get; init; } = string.Empty;
    }
}