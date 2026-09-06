using RemoteOS.Core.VirtualSystemDrive;
using Client.Services.VirtualSystemDrive;

VerifyDescriptorValidation();
VerifyRelativePathValidation();
await VerifyStorageBoundaryAsync();
Console.WriteLine("RemoteOS.Core VSD contract verification passed.");

static void VerifyDescriptorValidation()
{
    var builtIn = new ApplicationDescriptor(1, "remoteos.terminal", ApplicationDescriptorKind.BuiltIn,
        "Terminal", "1.0.0", new ApplicationDescriptorActivation(BuiltInKey: "terminal"));
    Assert(ApplicationDescriptorValidator.Validate(builtIn).IsValid, "Host-shaped BuiltIn descriptor was rejected.");

    var forgedBuiltIn = builtIn with
    {
        Activation = new ApplicationDescriptorActivation(BuiltInKey: "terminal", EntryAssembly: "lib/app.dll"),
    };
    Assert(ApplicationDescriptorValidator.Validate(forgedBuiltIn).ProblemCode == VirtualSystemDriveProblemCode.BuiltInMismatch,
        "BuiltIn descriptor was allowed to select an assembly.");

    var package = new ApplicationDescriptor(1, "com.example.hello", ApplicationDescriptorKind.Package,
        "Hello", "1.0.0", new ApplicationDescriptorActivation(EntryAssembly: "lib/net10.0/Hello.dll", EntryType: "Example.Hello"));
    Assert(ApplicationDescriptorValidator.Validate(package).IsValid, "Valid package descriptor was rejected.");
    Assert(ApplicationDescriptorValidator.Validate(package with { Kind = ApplicationDescriptorKind.BuiltIn }).ProblemCode
        == VirtualSystemDriveProblemCode.BuiltInMismatch, "Package descriptor could claim BuiltIn origin.");
    Assert(ApplicationDescriptorValidator.Validate(package with { SchemaVersion = 2 }).ProblemCode
        == VirtualSystemDriveProblemCode.SchemaUnsupported, "Unknown schema was accepted.");
    Assert(ApplicationDescriptorValidator.Validate(package with { Id = "RemoteOS.Forged" }).ProblemCode
        == VirtualSystemDriveProblemCode.AppIdInvalid, "Invalid AppId was accepted.");
}

static void VerifyRelativePathValidation()
{
    Assert(ApplicationDescriptorValidator.IsSafeRelativePath("lib/net10.0/App.dll"), "Safe relative path was rejected.");
    foreach (var unsafePath in new[] { "../outside.dll", "/absolute.dll", "C:/absolute.dll", "lib\\App.dll", "lib//App.dll", "lib/../App.dll" })
        Assert(!ApplicationDescriptorValidator.IsSafeRelativePath(unsafePath), $"Unsafe path '{unsafePath}' was accepted.");
}

static async Task VerifyStorageBoundaryAsync()
{
    var drive = new VirtualSystemDrive();
    drive.EnsureCreated();
    Assert(Directory.Exists(drive.BuiltInProgramsDirectory), "VSD did not create BuiltIn programs directory.");
    Assert(Directory.Exists(drive.ExternalProgramsDirectory), "VSD did not create External programs directory.");
    Assert(Directory.Exists(drive.ResolveRootChild($"Users/{drive.LocalProfileId}/Desktop")), "VSD did not create local Desktop directory.");

    var descriptorPath = drive.ResolveRootChild("System/descriptor-test.json");
    var descriptor = new ApplicationDescriptor(1, "com.example.storage", ApplicationDescriptorKind.Package,
        "Storage", "1.0.0", new ApplicationDescriptorActivation(EntryAssembly: "lib/net10.0/Storage.dll", EntryType: "Example.Storage"));
    await drive.WriteJsonAtomicallyAsync(descriptorPath, descriptor);
    var reread = await drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath);
    Assert(reread.Id == descriptor.Id && reread.Activation.EntryAssembly == descriptor.Activation.EntryAssembly,
        "Atomic descriptor write did not round-trip.");

    await File.WriteAllTextAsync(descriptorPath, """
        {"schemaVersion":1,"id":"com.example.storage","kind":"package","displayName":"Storage","version":"1.0.0","activation":{"entryAssembly":"lib/net10.0/Storage.dll","entryType":"Example.Storage"},"permissionModelVersion":2}
        """);
    var stringKind = await drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath);
    Assert(stringKind.Kind == ApplicationDescriptorKind.Package,
        "Descriptor did not accept the documented string kind value.");

    await File.WriteAllTextAsync(descriptorPath, """
        {"schemaVersion":1,"id":"com.example.storage","kind":"package","displayName":"Storage","version":"1.0.0","activation":{"entryAssembly":"lib/net10.0/Storage.dll","entryType":"Example.Storage"},"permissionModelVersion":2,"unexpected":true}
        """);
    await AssertProblemAsync(() => drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath),
        VirtualSystemDriveProblemCode.JsonInvalid);

    AssertProblem(() => drive.ResolveRootChild("../outside"), VirtualSystemDriveProblemCode.PathInvalid);
    AssertProblem(() => drive.ResolveRootChild("/outside"), VirtualSystemDriveProblemCode.PathInvalid);

    var link = Path.Combine(drive.ExternalProgramsDirectory, "escaped-link");
    var outside = Path.Combine(Path.GetTempPath(), $"remoteos-vsd-outside-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);
        AssertProblem(() => drive.ResolveUnder(drive.ExternalProgramsDirectory, "escaped-link/app.remoteos.json"),
            VirtualSystemDriveProblemCode.PathEscape);
    }
    finally
    {
        if (Directory.Exists(link) || File.Exists(link))
            File.Delete(link);
        if (Directory.Exists(outside))
            Directory.Delete(outside, recursive: true);
    }
}

static void AssertProblem(Action action, string expectedProblemCode)
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected VSD problem '{expectedProblemCode}'.");
    }
    catch (VirtualSystemDriveException exception) when (exception.ProblemCode == expectedProblemCode)
    {
    }
}

static async Task AssertProblemAsync(Func<Task> action, string expectedProblemCode)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected VSD problem '{expectedProblemCode}'.");
    }
    catch (VirtualSystemDriveException exception) when (exception.ProblemCode == expectedProblemCode)
    {
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
