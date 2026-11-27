using System.Net;
using OpenShock.Internal.Common.Problems;

namespace OpenShock.RepositoryServer.Errors;

public static class ModuleError
{
    public static OpenShockProblem ModuleNotFound => new("Module.NotFound", "The referenced module was not found", HttpStatusCode.NotFound);
    public static OpenShockProblem ModuleNotOwned => new("Module.NotOwned", "This repository is not authorized to publish to this module", HttpStatusCode.Forbidden);
    public static OpenShockProblem ZipMissing => new("Module.ZipMissing", "No file was uploaded", HttpStatusCode.BadRequest, "Expected a file field named 'zip'");
    public static OpenShockProblem ZipInvalid => new("Module.ZipInvalid", "The uploaded file is not a valid zip archive");
    public static OpenShockProblem ZipEmpty => new("Module.ZipEmpty", "The uploaded zip archive contains no entries");
    public static OpenShockProblem ZipPathTraversal => new("Module.ZipPathTraversal", "The zip archive contains entries with path traversal sequences");
    public static OpenShockProblem ZipDisallowedDirectory(string dir) => new("Module.ZipDisallowedDirectory", $"The zip archive contains a disallowed root directory: '{dir}'. Only 'wwwroot' and 'libs' are permitted");
    public static OpenShockProblem ZipDisallowedRootFile(string file) => new("Module.ZipDisallowedRootFile", $"The zip archive contains a disallowed root file: '{file}'. Only .dll, .pdb, and .json files are allowed at the root");
}
