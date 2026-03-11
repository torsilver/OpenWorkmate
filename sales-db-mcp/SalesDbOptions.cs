namespace SalesDbMcp;

/// <summary>
/// Options for the sales database connection.
/// Connection string is read from "SalesDb:ConnectionString" or env SALES_DB_CONNECTION_STRING.
/// </summary>
public class SalesDbOptions
{
    public const string SectionName = "SalesDb";

    public string ConnectionString { get; set; } = "";
}
