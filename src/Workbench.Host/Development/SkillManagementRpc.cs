using System.Text.Json;

internal sealed partial class WorkbenchHost
{
    private async Task<object> DispatchSkillManagementAsync(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "skills.catalog.search":
                return await _skills.SearchAsync(Optional(p, "query") ?? "", Optional(p, "sourceId") ?? Workbench.Tools.SkillManagementService.DefaultSource,
                    Integer(p, "offset", 0), Integer(p, "limit", 20), ct);
            case "project.skills.import":
                return await WithReviewMutationAsync(Required(p, "workspacePath"), async workspace =>
                    await _skills.ImportAsync(workspace, Required(p, "sourcePath"), ct), "importing or installing project skills");
            case "project.skills.install":
                return await WithReviewMutationAsync(Required(p, "workspacePath"), async workspace =>
                    await _skills.InstallAsync(workspace, Optional(p, "skillId"), Optional(p, "repository"), Optional(p, "revision"),
                        Optional(p, "skillPath"), ct), "importing or installing project skills");
            default: throw new ArgumentException("Unknown skill management request.");
        }
    }
}
