namespace ProductSearch.Api;

/// <summary>
/// Connection settings, read from environment variables with local-development
/// defaults. Nothing here is a secret - these are throwaway container credentials
/// - but keeping them out of source means you can point the tools at a different
/// host or password without editing code.
/// </summary>
public static class Env
{
    public static string ElasticsearchUrl =>
        Get("ELASTICSEARCH_URL", "http://localhost:9200");

    public static string ElasticsearchUsername =>
        Get("ELASTICSEARCH_USERNAME", "elastic");

    public static string ElasticsearchPassword =>
        Get("ELASTIC_PASSWORD", "changeme");

    public static string SqlConnectionString =>
        Get("SQLSERVER_CONNECTION", BuildSqlConnection("ProductCatalog"));

    public static string SqlMasterConnectionString =>
        Get("SQLSERVER_MASTER_CONNECTION", BuildSqlConnection("master"));

    private static string BuildSqlConnection(string database)
    {
        var host = Get("SQLSERVER_HOST", "localhost,1433");
        var user = Get("SQLSERVER_USER", "sa");
        var password = Get("MSSQL_SA_PASSWORD", "Str0ng!Passw0rd");
        return $"Server={host};Database={database};User Id={user};Password={password};"
             + "TrustServerCertificate=True;Encrypt=False";
    }

    private static string Get(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
