namespace TabularModelDeployer.Api.Models;

public class DeploymentRequest
{
    public string WorkspaceName { get; set; } = string.Empty;

    // Optional — if provided, skips the name→GUID lookup via Groups API.
    // Frontend already has this in sessionStorage as workspace.id.
    public string? WorkspaceId { get; set; }

    public string SourceType { get; set; } = string.Empty;
    public Dictionary<string, string> ConnectionParams { get; set; } = new();
    public SourceCredential? Credential { get; set; }
    public ModelSchema ModelSchema { get; set; } = new();
}

// public class DeploymentRequest
// {
//     public string WorkspaceName { get; set; } = string.Empty;

//     // Which connector to use — must match a registered ISourceConnector.SourceType
//     // (e.g. "AzureSql", "Snowflake"). Adding a new source type means adding a new
//     // ISourceConnector implementation, not a new field here.
//     public string SourceType { get; set; } = string.Empty;

//     // Generic key/value bag for whatever the chosen connector needs.
//     // AzureSql expects: "server", "database"
//     // Snowflake expects: "account", "warehouse", "database", "schema" (and optionally "role")
//     public Dictionary<string, string> ConnectionParams { get; set; } = new();

//     // Credentials for the DirectQuery data source itself.
//     // These authenticate the *dataset* to the source at query time —
//     // separate from the PowerBI:ClientId/Secret in appsettings.json,
//     // which only authenticate the XMLA/deployment connection.
//     public SourceCredential? Credential { get; set; }

//     public ModelSchema ModelSchema { get; set; } = new();
// }

public class SourceCredential
{
    // Which credential shape this is — the connector's BuildCredential
    // decides what it does with AuthType + Values based on this.
    // e.g. "UsernamePassword", "ServicePrincipal", "Key"
    public string AuthType { get; set; } = "UsernamePassword";

    // Generic key/value bag for whatever the auth type needs.
    // UsernamePassword expects: "username", "password"
    // ServicePrincipal expects: "clientId", "clientSecret", "tenantId"
    // Key (key-pair auth) expects: "username", "privateKey", "passphrase" (optional)
    public Dictionary<string, string> Values { get; set; } = new();
}

public class ModelSchema
{
    public string Model_Name { get; set; } = string.Empty;
    public List<TableConfig> Tables { get; set; } = new();
    public List<RelationshipConfig> Relationships { get; set; } = new();
}

public class TableConfig
{
    public string Name { get; set; } = string.Empty;
    public bool Is_Physical { get; set; }
    public List<ColumnConfig> Columns { get; set; } = new();
    public List<MeasureConfig>? Measures { get; set; }
}

public class ColumnConfig
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool? IsHidden { get; set; }
}

public class MeasureConfig
{
    public string Name { get; set; } = string.Empty;
    public string Expression { get; set; } = string.Empty;
    public string? DataType { get; set; }
    public string? Role { get; set; }
    public string? DefaultFormat { get; set; }
}

public class RelationshipConfig
{
    public string Name { get; set; } = string.Empty;
    public string From_Table { get; set; } = string.Empty;
    public string From_Col { get; set; } = string.Empty;
    public string To_Table { get; set; } = string.Empty;
    public string To_Col { get; set; } = string.Empty;
}