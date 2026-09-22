using Microsoft.AnalysisServices.Tabular;
using TabularModelDeployer.Api.Models;

namespace TabularModelDeployer.Api.Services;

/// <summary>
/// One implementation per data source type. Configures the TOM DataSource
/// (connection details + credential) and builds the per-table M expression
/// for DirectQuery partitions. Register new sources in SourceConnectorRegistry below.
/// </summary>
public interface ISourceConnector
{
    string SourceType { get; }

    /// <summary>Sets ConnectionDetails.Protocol / Address on the data source.</summary>
    void ConfigureDataSource(StructuredDataSource dataSource, Dictionary<string, string> connectionParams);

    /// <summary>Builds the Credential object for this source's supported auth type(s).</summary>
    Credential BuildCredential(SourceCredential credential);

    /// <summary>Builds the M expression for a DirectQuery partition against one table.</summary>
    string BuildMExpression(Dictionary<string, string> connectionParams, string tableName);
}

public class AzureSqlConnector : ISourceConnector
{
    public string SourceType => "AzureSql";

    public void ConfigureDataSource(StructuredDataSource dataSource, Dictionary<string, string> p)
    {
        dataSource.ConnectionDetails.Protocol = "tds";
        dataSource.ConnectionDetails.Address["server"] = p["server"];
        dataSource.ConnectionDetails.Address["database"] = p["database"];
    }

    public Credential BuildCredential(SourceCredential cred) => new Credential
    {
        AuthenticationKind = AuthenticationKind.UsernamePassword,
        Username = cred.Values["username"],
        Password = cred.Values["password"],
        PrivacySetting = "Organizational",
        EncryptConnection = true
    };

    public string BuildMExpression(Dictionary<string, string> p, string tableName) => $@"
let
    Source = Sql.Database(""{p["server"]}"", ""{p["database"]}""),
    Nav = Source{{[Schema=""dbo"",Item=""{tableName}""]}}[Data]
in
    Nav";
}

public class SnowflakeConnector : ISourceConnector
{
    public string SourceType => "Snowflake";

    public void ConfigureDataSource(StructuredDataSource dataSource, Dictionary<string, string> p)
    {
        dataSource.ConnectionDetails.Protocol = "Snowflake.Databases";
        dataSource.ConnectionDetails.Address["account"] = p["account"];
        dataSource.ConnectionDetails.Address["warehouse"] = p["warehouse"];
        dataSource.ConnectionDetails.Address["database"] = p["database"];
        dataSource.ConnectionDetails.Address["schema"] = p["schema"];
    }

    public Credential BuildCredential(SourceCredential cred) => cred.AuthType switch
    {
        "UsernamePassword" => new Credential
        {
            AuthenticationKind = AuthenticationKind.UsernamePassword,
            Username = cred.Values["username"],
            Password = cred.Values["password"],
            PrivacySetting = "Organizational",
            EncryptConnection = true
        },
        // NOTE: Key-pair auth is NOT currently supported for Power BI cloud
        // connections (confirmed — works in Desktop, rejected in Service),
        // so a "Key" branch isn't wired up here yet. Revisit if that changes.
        _ => throw new ArgumentException($"Unsupported Snowflake AuthType '{cred.AuthType}'.")
    };

    public string BuildMExpression(Dictionary<string, string> p, string tableName) => $@"
let
    Source = Snowflake.Databases(""{p["account"]}"", ""{p["warehouse"]}"", [Implementation=""2.0""]),
    Database1 = Source{{[Name = ""{p["database"]}"", Kind = ""Database""]}}[Data],
    Schema1 = Database1{{[Name = ""{p["schema"]}"", Kind = ""Schema""]}}[Data],
    Table1 = Schema1{{[Name = ""{tableName}"", Kind = ""Table""]}}[Data]
in
    Table1";
}

/// <summary>
/// Looks up the right connector by SourceType string from the request.
/// Add new ISourceConnector implementations to the list in the constructor.
/// </summary>
public class SourceConnectorRegistry
{
    private readonly Dictionary<string, ISourceConnector> _connectors;

    public SourceConnectorRegistry()
    {
        var all = new ISourceConnector[]
        {
            new AzureSqlConnector(),
            new SnowflakeConnector()
            // Add new connectors here, e.g. new PostgresConnector(), new RedshiftConnector()
        };
        _connectors = all.ToDictionary(c => c.SourceType, c => c, StringComparer.OrdinalIgnoreCase);
    }

    public ISourceConnector Get(string sourceType)
    {
        if (!_connectors.TryGetValue(sourceType, out var connector))
        {
            var known = string.Join(", ", _connectors.Keys);
            throw new ArgumentException($"Unknown SourceType '{sourceType}'. Known types: {known}.");
        }
        return connector;
    }
}