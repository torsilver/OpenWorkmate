namespace AccurateDataMcp;

/// <summary>
/// Options for the accurate data file store.
/// Directory is read from "AccurateData:Directory" or env ACCURATE_DATA_DIRECTORY.
/// </summary>
public class AccurateDataOptions
{
    public const string SectionName = "AccurateData";

    /// <summary>Root directory for accurate data files. Defaults to %LocalAppData%/OfficeCopilot/AccurateData if not set.</summary>
    public string Directory { get; set; } = "";
}
