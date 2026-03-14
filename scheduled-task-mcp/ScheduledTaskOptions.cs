namespace ScheduledTaskMcp;

/// <summary>
/// Options for the scheduled task file store.
/// Directory is read from "ScheduledTasks:Directory" or env SCHEDULED_TASKS_DIRECTORY.
/// </summary>
public class ScheduledTaskOptions
{
    public const string SectionName = "ScheduledTasks";

    /// <summary>Root directory for .task.md and .meta.json files.</summary>
    public string Directory { get; set; } = "";
}
