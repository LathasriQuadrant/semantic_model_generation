using Microsoft.AnalysisServices.Tabular;
using TabularModelDeployer.Api.Models;
using System.Linq;

namespace TabularModelDeployer.Api.Services;

public class TabularDeploymentService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TabularDeploymentService> _logger;
    private readonly SourceConnectorRegistry _connectors = new();
    private readonly PowerBiRestCredentialService _credentialService;

    public TabularDeploymentService(
        IConfiguration config,
        PowerBiRestCredentialService credentialService,
        ILogger<TabularDeploymentService> logger)
    {
        _config = config;
        _credentialService = credentialService;
        _logger = logger;
    }

    // Maps the "type" string coming from the incoming JSON payload
    // (string, int64, double, boolean, dateTime, date) to a Tabular DataType.
    private static DataType MapColumnType(string type) => type.ToLower() switch
    {
        "double" => DataType.Double,
        "int64" => DataType.Int64,
        "boolean" => DataType.Boolean,
        "datetime" => DataType.DateTime,
        "date" => DataType.DateTime,
        _ => DataType.String
    };

    public async Task<string> DeployModel(DeploymentRequest request)
    {
        try
        {
            // 🔒 Service Principal from appsettings.json
            var tenantId = _config["PowerBI:TenantId"];
            var clientId = _config["PowerBI:ClientId"];
            var clientSecret = _config["PowerBI:ClientSecret"];

            // 🔄 Dynamic values from endpoint
            var workspaceUrl = $"powerbi://api.powerbi.com/v1.0/myorg/{request.WorkspaceName}";
            var connector = _connectors.Get(request.SourceType);

            string conn = $"Provider=MSOLAP;Data Source={workspaceUrl};" +
                          $"User ID=app:{clientId}@{tenantId};Password={clientSecret};";

            _logger.LogInformation($"Connecting to workspace: {request.WorkspaceName}");
            var server = new Server();
            server.Connect(conn);

            var schema = request.ModelSchema;
            var db = server.Databases.FindByName(schema.Model_Name);

            // ---------------- DATABASE CREATION --------------//
            if (db == null)
            {
                _logger.LogInformation($"Creating new database: {schema.Model_Name}");
                db = new Database
                {
                    Name = schema.Model_Name,
                    ID = schema.Model_Name,
                    CompatibilityLevel = 1500
                };
                db.Model = new Model
                {
                    Name = schema.Model_Name
                };
                server.Databases.Add(db);
                db.Update(Microsoft.AnalysisServices.UpdateOptions.ExpandFull);
            }

            var model = db.Model;

            // ---------------- DATA SOURCE + CREDENTIAL (required for DirectQuery) ----------------
            // Declaring ModeType.DirectQuery on a partition is not enough on its own —
            // the model needs a registered DataSource with bound credentials, or queries
            // will fail at runtime with "credentials not provided" even though deployment succeeds.
            const string dataSourceName = "PrimaryDataSource";
            var dataSource = model.DataSources.Find(dataSourceName) as StructuredDataSource;

            if (dataSource == null && request.Credential != null)
            {
                _logger.LogInformation("Configuring data source and credentials");
                dataSource = new StructuredDataSource
                {
                    Name = dataSourceName
                };
                model.DataSources.Add(dataSource);

                // ConnectionDetails.Address is not directly assignable — it's a
                // mutable key/value object you write into via its indexer.
                // Delegated to the connector so each source type sets the fields it needs.
                connector.ConfigureDataSource(dataSource, request.ConnectionParams);

                // Delegated too — different sources support different auth types
                // (e.g. Snowflake currently only UsernamePassword; key-pair isn't
                // supported for Power BI cloud connections yet).
                dataSource.Credential = connector.BuildCredential(request.Credential);
            }
            // NOTE: For datasets already published to the Power BI service, credential
            // binding for DirectQuery sources is sometimes only honored via the Power BI
            // REST API (Datasets - Update Datasource Credentials / gateway datasources)
            // rather than through TOM's Model.DataSources. If SaveChanges() succeeds but
            // queries still fail with a credentials error, that REST call is the fallback.

            // IMPORTANT: Do NOT clear tables/relationships every time unless doing full rebuild
            // model.Tables.Clear();
            // model.Relationships.Clear();
            // model.SaveChanges();

            // ---------------- TABLE CREATION / UPDATE ----------------
            _logger.LogInformation($"Creating/updating {schema.Tables.Count} tables");
            foreach (var t in schema.Tables)
            {
                var table = model.Tables.Find(t.Name);
                bool tableWasNew = false;

                if (table == null)
                {
                    table = new Table { Name = t.Name };
                    tableWasNew = true;
                    model.Tables.Add(table);
                }

                bool isMeasureOnlyTable =
                    (t.Columns == null || t.Columns.Count == 0) &&
                    (t.Measures != null && t.Measures.Count > 0);

                // 1️⃣ Add / Update Columns
                if (t.Columns != null)
                {
                    foreach (var c in t.Columns)
                    {
                        var existing = table.Columns.Find(c.Name) as DataColumn;

                        if (existing != null)
                        {
                            // Update existing column
                            existing.DataType = MapColumnType(c.Type);
                            existing.SourceColumn = c.Name;
                            if (c.IsHidden == true) existing.IsHidden = true;
                        }
                        else
                        {
                            var column = new DataColumn
                            {
                                Name = c.Name,
                                SourceColumn = c.Name,
                                DataType = MapColumnType(c.Type)
                            };
                            if (c.IsHidden == true) column.IsHidden = true;
                            table.Columns.Add(column);
                        }
                    }
                }

                // 🔥 If measure-only table AND no visible columns exist → add dummy
                if (isMeasureOnlyTable && !table.Columns.Any(col => !col.IsHidden))
                {
                    if (table.Columns.Find("DummyColumn") == null)
                    {
                        var dummy = new DataColumn
                        {
                            Name = "DummyColumn",
                            DataType = DataType.String,
                            IsHidden = true
                        };
                        table.Columns.Add(dummy);
                    }
                }

                // 2️⃣ Create / Update Partition (simplified – overwrites if exists)
                var partition = table.Partitions.Find("MainPartition");
                if (partition == null)
                {
                    partition = new Partition { Name = "MainPartition" };
                    table.Partitions.Add(partition);
                }

                partition.Mode = t.Is_Physical ? ModeType.DirectQuery : ModeType.Import;

                var msource = new MPartitionSource();
                if (t.Is_Physical)
                {
                    msource.Expression = connector.BuildMExpression(request.ConnectionParams, t.Name);
                }
                else
                {
                    msource.Expression = @"
let
    Source = #table(
        {""ORDER_AMOUNT""},
        {
            {100},
            {200},
            {300}
        }
    )
in
    Source";
                }
                partition.Source = msource;

                // 3️⃣ Add / Update Measures
                if (t.Measures != null)
                {
                    foreach (var m in t.Measures)
                    {
                        var existingMeasure = table.Measures.Find(m.Name);

                        if (existingMeasure != null)
                        {
                            // Update existing measure
                            existingMeasure.Expression = m.Expression;
                            if (!string.IsNullOrWhiteSpace(m.DefaultFormat))
                                existingMeasure.FormatString = m.DefaultFormat;
                        }
                        else
                        {
                            var measure = new Measure
                            {
                                Name = m.Name,
                                Expression = m.Expression
                            };

                            if (!string.IsNullOrWhiteSpace(m.DefaultFormat))
                                measure.FormatString = m.DefaultFormat;

                            table.Measures.Add(measure);
                        }
                    }
                }
            }

            // ---------------- RELATIONSHIPS ----------------
            _logger.LogInformation($"Creating/updating {schema.Relationships.Count} relationships");
            foreach (var r in schema.Relationships)
            {
                var fromTable = model.Tables.Find(r.From_Table);
                var toTable = model.Tables.Find(r.To_Table);

                if (fromTable == null || toTable == null) continue;

                var fromColumn = fromTable.Columns.Find(r.From_Col) as DataColumn;
                var toColumn = toTable.Columns.Find(r.To_Col) as DataColumn;

                if (fromColumn == null || toColumn == null) continue;

                var existingRel = model.Relationships
                    .OfType<SingleColumnRelationship>()
                    .FirstOrDefault(rel =>
                        rel.FromColumn == fromColumn &&
                        rel.ToColumn == toColumn);

                if (existingRel == null)
                {
                    var relationship = new SingleColumnRelationship
                    {
                        Name = r.Name ?? $"{fromColumn.Name}_to_{toColumn.Name}",
                        FromColumn = fromColumn,
                        ToColumn = toColumn
                        // IsActive = true, CrossFilteringBehavior = etc. if needed
                    };
                    model.Relationships.Add(relationship);
                }
                // else → could update properties if needed
            }

            // ---------- DEBUG: log what will be sent to Power BI ----------
            // NOTE: the data source JSON may contain the credential/password.
            // Redact it before sharing these logs, and remove this block when done.
            foreach (var ds in model.DataSources.OfType<StructuredDataSource>())
            {
                _logger.LogInformation("DataSource JSON: {Json}",
                    Microsoft.AnalysisServices.Tabular.JsonSerializer.SerializeObject(ds));
            }

            foreach (var tbl in model.Tables)
            {
                foreach (var p in tbl.Partitions)
                {
                    if (p.Source is MPartitionSource ms)
                        _logger.LogInformation("M expression for {Table}: {Expr}", tbl.Name, ms.Expression);
                }
            }
            // ---------- END DEBUG ----------

            // 🔥 SAVE (with retry)
            _logger.LogInformation("Saving model changes...");
            int saveAttempts = 0;
            int maxSaveAttempts = 3;

            while (saveAttempts < maxSaveAttempts)
            {
                try
                {
                    saveAttempts++;
                    _logger.LogInformation($"SaveChanges attempt {saveAttempts}/{maxSaveAttempts}");
                    model.SaveChanges();
                    _logger.LogInformation("Model changes saved successfully");
                    break; // Success, exit loop
                }
                catch (Microsoft.AnalysisServices.OperationException ex) when (saveAttempts < maxSaveAttempts)
                {
                    _logger.LogWarning($"SaveChanges attempt {saveAttempts} failed: {ex.Message}. Retrying...");
                    await Task.Delay(2000); // Wait 2 seconds before retry
                }
                catch (Microsoft.AnalysisServices.OperationException ex)
                {
                    _logger.LogError($"SaveChanges failed after {maxSaveAttempts} attempts: {ex.Message}");
                    throw;
                }
            }

            // 🔥 REMOVE DUMMY COLUMNS FROM MEASURE-ONLY TABLES
            bool needsFinalSave = false;
            foreach (var table in model.Tables)
            {
                var dummy = table.Columns.Find("DummyColumn");
                if (dummy != null && table.Measures.Any())
                {
                    table.Columns.Remove(dummy);
                    needsFinalSave = true;
                }
            }

            if (needsFinalSave)
            {
                _logger.LogInformation("Removing dummy columns...");
                int finalSaveAttempts = 0;
                int maxFinalAttempts = 3;

                while (finalSaveAttempts < maxFinalAttempts)
                {
                    try
                    {
                        finalSaveAttempts++;
                        _logger.LogInformation($"Final SaveChanges attempt {finalSaveAttempts}/{maxFinalAttempts}");
                        model.SaveChanges();
                        _logger.LogInformation("Dummy columns removed and saved");
                        break; // Success, exit loop
                    }
                    catch (Microsoft.AnalysisServices.OperationException ex) when (finalSaveAttempts < maxFinalAttempts)
                    {
                        _logger.LogWarning($"Final SaveChanges attempt {finalSaveAttempts} failed: {ex.Message}. Retrying...");
                        await Task.Delay(2000); // Wait 2 seconds before retry
                    }
                    catch (Microsoft.AnalysisServices.OperationException ex)
                    {
                        _logger.LogError($"Final SaveChanges failed after {maxFinalAttempts} attempts: {ex.Message}");
                        throw;
                    }
                }
            }

            server.Disconnect();
            // ---------------- BIND CREDENTIALS VIA REST API ----------------
            // TOM's inline Credential (set above on dataSource.Credential) only
            // satisfies deploy-time validation — it does not register a
            // discoverable datasource object in the Power BI Service. Without
            // this call, tables/schema appear but DirectQuery returns no data.
            if (request.Credential != null)
            {
                _logger.LogInformation("Binding credentials via Power BI REST API...");
                await _credentialService.BindCredentialsAsync(
                    request.WorkspaceName,
                    request.WorkspaceId,
                    schema.Model_Name,
                    request.Credential
                );
                _logger.LogInformation("Credentials bound successfully");
            }

            return "🔥 Model deployed/updated successfully!";
        }
        catch (Exception ex)
        {
            _logger.LogError($"Deployment error: {ex.Message}\n{ex.StackTrace}");
            throw;
        }
    }
}
