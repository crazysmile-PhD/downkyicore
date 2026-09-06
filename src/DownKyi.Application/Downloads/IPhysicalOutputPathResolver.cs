namespace DownKyi.Application.Downloads;

/// <summary>
/// Resolves a logical output base path to the concrete path that subsequent
/// file-system operations must use for one workflow.
/// </summary>
public interface IPhysicalOutputPathResolver
{
    string ResolvePhysicalBasePath(string logicalBasePath);
}
