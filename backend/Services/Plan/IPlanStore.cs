namespace OpenWorkmate.Server.Services.Plan;

/// <summary>计划存储：基于文件的计划 CRUD。</summary>
public interface IPlanStore
{
    Task<IReadOnlyList<PlanMeta>> ListAsync(CancellationToken ct = default);
    Task<(string Content, PlanMeta Meta)?> GetAsync(string planId, CancellationToken ct = default);
    Task<string> SaveAsync(string planId, string content, PlanMeta? meta = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string planId, CancellationToken ct = default);
}
