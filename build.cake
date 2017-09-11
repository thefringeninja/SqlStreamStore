#addin "Cake.FileHelpers"

var target          = Argument("target", "Default");
var configuration   = Argument("configuration", "Release");
var artifactsDir    = Directory("./artifacts");
var solution        = "./src/SqlStreamStore.sln";
var buildNumber     = string.IsNullOrWhiteSpace(EnvironmentVariable("BUILD_NUMBER")) ? "0" : EnvironmentVariable("BUILD_NUMBER");

Task("Clean")
    .Does(() =>
{
    CleanDirectory(artifactsDir);
});

Task("RestorePackages")
    .IsDependentOn("Clean")
    .Does(() =>
{
	DotNetCoreRestore(solution);
});

Task("Build")
    .IsDependentOn("RestorePackages")
    .Does(() =>
{
	var settings = new DotNetCoreBuildSettings
	{
		Configuration = configuration
	};

	DotNetCoreBuild(solution, settings);
});

Task("RunTests")
    .IsDependentOn("Build")
    .Does(() =>
{
    var testProjects = new string[] {
        "SqlStreamStore.Tests",
        "SqlStreamStore.MsSql.Tests",
        "SqlStreamStore.MySql.Tests"
    };

    foreach(var testProject in testProjects)
    {
        var projectDir = "./src/" + testProject + "/";
        var projectFile = testProject + ".csproj";
        var settings = new DotNetCoreTestSettings
        {
            Configuration = configuration,
            WorkingDirectory = projectDir
        };
        DotNetCoreTest(projectFile, settings);
    }
});

Task("DotNetPack")
    .IsDependentOn("Build")
    .Does(() =>
{
    var versionSuffix = "build" + buildNumber.ToString().PadLeft(5, '0');

    var dotNetCorePackSettings   = new DotNetCorePackSettings
    {
        OutputDirectory = artifactsDir,
		NoBuild = true,
		Configuration = configuration,
        VersionSuffix = versionSuffix
    };
    
	DotNetCorePack("./src/SqlStreamStore", dotNetCorePackSettings);
	DotNetCorePack("./src/SqlStreamStore.MsSql", dotNetCorePackSettings);
	DotNetCorePack("./src/SqlStreamStore.MySql", dotNetCorePackSettings);
});

Task("Default")
    .IsDependentOn("RunTests")
    .IsDependentOn("DotNetPack");

RunTarget(target);
