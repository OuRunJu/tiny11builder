using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Tiny11Builder.Core;

public sealed partial class BuildEngine
{
    private readonly IProcessRunner runner;
    private readonly IMediaSourceFactory mediaFactory;
    private readonly IOfflineRegistry registry;
    private readonly IImageOperations imageOperations;
    private readonly IBuildHost host;
    private readonly BuildEngineOptions options;

    public BuildEngine(IProcessRunner runner, IMediaSourceFactory mediaFactory, IOfflineRegistry registry,
        IImageOperations imageOperations, IBuildHost host, BuildEngineOptions? options = null)
    {
        this.runner = runner;
        this.mediaFactory = mediaFactory;
        this.registry = registry;
        this.imageOperations = imageOperations;
        this.host = host;
        this.options = options ?? new();
    }
    public async Task<IReadOnlyList<WindowsImage>> InspectAsync(string source, string dismPath, CancellationToken token)
    {
        PathSafety.RejectReparsePoints(source);
        using var media = mediaFactory.Open(PathSafety.Full(source));
        return await new DismClient(dismPath, runner).GetImagesAsync(InstallationMedia.ImageFile(media.RootPath), token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageInventoryItem>> InventoryAsync(BuildRecipe recipe,
        IProgress<BuildProgress>? progress, CancellationToken token)
    {
        recipe.ValidateOptions();
        PathSafety.ValidateWorkspace(recipe.SourcePath, recipe.WorkspacePath);
        host.ValidateTools(recipe with { OscdimgPath = "" });
        using var media = mediaFactory.Open(recipe.SourcePath);
        var dism = new DismClient(recipe.DismPath, runner);
        var sourceWim = InstallationMedia.ImageFile(media.RootPath);
        var image = await dism.GetImageAsync(sourceWim, recipe.ImageIndex, token).ConfigureAwait(false);
        host.ValidateInventory(recipe with { OutputPath = "" }, image);
        // A read-only inventory never uses lazy mounting: it has no 87/4350 fallback, and a failed probe costs minutes.
        var execution = BuildExecutionPlan.Create(recipe, Environment.ProcessorCount, allowOptimizedMount: false);
        using var job = BuildJob.Create(recipe.WorkspacePath, "inventory", execution.BufferedLog);
        var scratch = Path.Combine(job.Directory, "scratch");
        Directory.CreateDirectory(scratch);
        var inventoryDism = new DismClient(recipe.DismPath, runner, text =>
        {
            job.Log(text);
            progress?.Report(new("Inventory", text));
        }, new(scratch, Path.Combine(job.Directory, "dism.log"), OptimizedMount: false));
        var mount = Path.Combine(job.Directory, "mount");
        Directory.CreateDirectory(mount);
        var attempted = false;
        try
        {
            progress?.Report(new("Inventory", "正在以只读方式挂载映像以列出其组件。"));
            var index = recipe.ImageIndex;
            var wim = sourceWim;
            if (Path.GetExtension(sourceWim).Equals(".esd", StringComparison.OrdinalIgnoreCase))
            {
                wim = Path.Combine(job.Directory, "working.wim");
                await inventoryDism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{sourceWim}", $"/SourceIndex:{index}",
                    $"/DestinationImageFile:{wim}", "/Compress:fast", "/CheckIntegrity").ConfigureAwait(false);
                index = 1;
            }
            attempted = true;
            await inventoryDism.RunAsync(token, "/Mount-Image", $"/ImageFile:{wim}", $"/Index:{index}",
                $"/MountDir:{mount}", "/ReadOnly", "/CheckIntegrity").ConfigureAwait(false);
            var result = await ImageInventory.ReadAsync(inventoryDism, imageOperations, mount, token).ConfigureAwait(false);
            await inventoryDism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
            attempted = false;
            token.ThrowIfCancellationRequested();
            job.State("completed");
            PathSafety.DeleteOwnedDirectory(mount, job.Directory);
            PathSafety.DeleteOwnedDirectory(scratch, job.Directory);
            DeleteWorkingWim(job.Directory);
            return result;
        }
        catch (Exception error)
        {
            if (error is DismCommandException && OperatingSystem.IsWindows() &&
                File.Exists(recipe.DismPath) && !ToolLocator.SupportsServicing(recipe.DismPath, image))
                error = new ServicingToolsRequiredException("The installed image tools could not read this image's components. " +
                    "Install current Windows ADK Deployment Tools and servicing updates, then reopen the app and retry.", error);
            // Recovery first: nothing fallible (log, flush, job marker) may run before an owned mount is discarded.
            var cancelled = BuildFailureException.IsCancellationCause(error);
            var secondary = new List<Exception>();
            Exception? discardFailure = null;
            void Guard(Action step)
            {
                try { step(); }
                catch (Exception diagnostic) { secondary.Add(diagnostic); }
            }
            if (attempted)
            {
                if (cancelled) Guard(() => progress?.Report(new("Cancel cleanup", "正在丢弃只读工作挂载",
                    OperationId: "cancel:mount", State: BuildOperationState.Running)));
                discardFailure = await TryDiscardAsync(inventoryDism, mount).ConfigureAwait(false);
                if (discardFailure is not null) secondary.Add(discardFailure);
            }
            if (cancelled) Guard(() => progress?.Report(new("Cancel cleanup", "正在保存日志与已停止作业状态",
                OperationId: "cancel:logs", State: BuildOperationState.Running)));
            Guard(() => job.Log(error.Message));
            if (discardFailure is not null) Guard(() => job.Log(DiscardFailureText(discardFailure)));
            Guard(job.Flush);
            Guard(() => job.State(discardFailure is not null ? "failed" : cancelled ? "cancelled" : "failed",
                discardFailure is not null ? "Recovery required" : null));
            var kind = cancelled ? "cancelled" : "failed";
            throw new BuildFailureException(
                $"Inventory {kind}. The source was not changed. Logs and working files are retained at {job.Directory}. {error.Message}" +
                DiscardText(discardFailure, resumable: false) + SecondaryText(secondary, discardFailure),
                job.Directory, secondary.Count == 0 ? error : new AggregateException([error, .. secondary]),
                cancelled, discardFailure is not null);
        }
    }

    public async Task<BuildResult> BuildAsync(BuildRecipe recipe, IProgress<BuildProgress>? progress, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        PathSafety.Validate(recipe);
        if (string.IsNullOrEmpty(recipe.OscdimgPath)) throw new FileNotFoundException("创建可启动 ISO 需要 Windows ADK oscdimg.exe。");
        host.ValidateTools(recipe);
        using var media = mediaFactory.Open(recipe.SourcePath);
        var sourceWim = InstallationMedia.ImageFile(media.RootPath);
        var workingCompression = InstallationMedia.WorkingCompression(sourceWim);
        var execution = BuildExecutionPlan.Create(recipe, Environment.ProcessorCount, options.AllowOptimizedMount);
        var preparationNotes = new List<string>();
        if (execution.OptimizedMount && MountOptimizationCache.IsUnavailable(recipe.WorkspacePath, recipe.DismPath, preparationNotes.Add))
            execution = execution with { OptimizedMount = false };
        workingCompression = execution.WorkingCompression(workingCompression);
        var metadataDism = new DismClient(recipe.DismPath, runner);
        var image = await metadataDism.GetImageAsync(sourceWim, recipe.ImageIndex, token).ConfigureAwait(false);
        host.ValidateImage(recipe, image);
        var efiRelative = new[] { @"efi\microsoft\boot\efisys.bin", @"efi\microsoft\boot\efisys_noprompt.bin" }
            .FirstOrDefault(p => File.Exists(Path.Combine(media.RootPath, p))) ??
            throw new InvalidDataException("源不包含受支持的 UEFI 启动映像。");
        var sourceInstall = new FileInfo(sourceWim);
        using var job = BuildJob.Create(recipe.WorkspacePath, "build", execution.BufferedLog);
        var staging = Path.Combine(job.Directory, "media");
        var mount = Path.Combine(job.Directory, "mount");
        var workingWim = Path.Combine(job.Directory, "working.wim");
        var temporaryIso = Path.Combine(job.Directory, "output.iso");
        var reportPath = Path.Combine(job.Directory, "report.json");
        var scratch = Path.Combine(job.Directory, "scratch");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(scratch);
        IReadOnlyDictionary<string, string>? prefetchedFeatures = null;
        var tracking = new BuildOperationTracker(progress, job.Log);
        var dism = new DismClient(recipe.DismPath, runner, tracking.Output,
            new(scratch, Path.Combine(job.Directory, "dism.log"), execution.OptimizedMount));
        var session = new BuildSession
        {
            Recipe = recipe, Job = job, Tracking = tracking, CancellationProgress = progress, Dism = dism, Execution = execution, Image = image,
            EfiRelative = efiRelative, Staging = staging, Mount = mount, WorkingWim = workingWim, TemporaryIso = temporaryIso,
            ReportPath = reportPath, Scratch = scratch, Elapsed = elapsed, UsedOptimizedMount = execution.OptimizedMount,
            SetupPending = SetupInstaller.RequiresBootEdit(recipe)
        };
        void Log(string text) => session.Log(text);
        void Stage(string next, string description) => session.Stage(next, description, token);
        try
        {
            foreach (var note in preparationNotes)
            {
                job.Log(note);
                progress?.Report(new("Prepare", note));
            }
            recipe.Write(Path.Combine(job.Directory, JobCheckpoint.RecipeFileName));
            var servicingActions = recipe.ExpertActions.Where(action => action.Kind is ImageItemKind.Package or
                ImageItemKind.ProvisionedApp or ImageItemKind.Capability or ImageItemKind.Feature).ToArray();
            foreach (var (name, title) in new[]
            {
                ("Copy media", "复制安装介质"), ("Export edition", "导出所选版本"),
                ("Mount", "挂载工作映像"), ("Inventory", "验证单项映像操作"),
                ("Custom servicing", "应用单项维护操作"), ("Remove apps", "移除所选应用"),
                ("Optional features", "移除可选功能与能力"),
                ("Component cleanup", "清理已被取代的组件文件"), (".NET 3.5", "包含 .NET Framework 3.5"),
                ("Windows settings", "应用 Windows 设置"), ("Advanced payloads", "移除所选资源包文件"),
                ("Core pruning", "修剪 Core 组件资源包"), ("Redundant data", "移除离线冗余数据"),
                ("Commit", "保存映像更改"),
                ("Setup image", "应用 Windows 安装程序设置"), ("Compress", "压缩最终映像"),
                ("Create ISO", "创建可启动 ISO"), ("Verify output", "验证并发布 ISO")
            }.Where(entry => entry.Item1 switch
            {
                "Inventory" => recipe.ExpertActions.Length > 0,
                "Custom servicing" => servicingActions.Length > 0,
                ".NET 3.5" => recipe.EnableNetFx3,
                "Component cleanup" => recipe.SelectedOptions.Contains("cleanup") || recipe.Flavor == BuildFlavor.Core,
                "Core pruning" => recipe.Flavor == BuildFlavor.Core,
                "Redundant data" => recipe.RemoveRedundantData,
                "Setup image" => SetupInstaller.RequiresBootEdit(recipe),
                _ => true
            })) tracking.Plan("stage:" + name, name, title);
            Log($"目标：{image.Summary}。源介质永不修改。");
            Log($"Execution: {(recipe.TurboMode ? "Turbo" : "标准")}; 优化挂载={execution.OptimizedMount} " +
                $"({(!recipe.TurboMode ? "Turbo 已关闭" : !options.AllowOptimizedMount ? "延迟挂载为可选且未启用" : "主机已启用")}); " +
                $"复制工作线程={execution.CopyWorkers}; 中间={workingCompression}.");
            Log("策略以离线注册表值验证，不保证 Windows 行为。构建/版本支持因版本而异。");
            if (image.Version.Build > 26100) Log("较新/Insider 构建需要匹配的工具；不暗示启动认证。");
            if (recipe.Flavor == BuildFlavor.Core || recipe.ExpertActions.Length > 0)
                Log("警告：Core/专家输出可能无法启动、更新、恢复或提供防病毒保护。请在隔离虚拟机中测试。");
            Stage("Copy media", "正在复制安装介质（不含原始多版本安装映像）。");
            session.StagingResult = await MediaStager.CopyAsync(media.RootPath, staging, execution.CopyWorkers,
                recipe.OmitNetFx3SetupPayload, Log, token).ConfigureAwait(false);
            Stage("Export edition", "正在将所选版本导出为可写 WIM。");
            Log(execution.FastWorkingImage ?
                "Turbo：使用快速中间压缩以加速提交。最终压缩仍按所选方式。" :
                $"中间压缩：{workingCompression}。保留现有 WIM 压缩以避免重新压缩源。");
            await dism.RunAsync(token, "/Export-Image", $"/SourceImageFile:{sourceWim}", $"/SourceIndex:{recipe.ImageIndex}",
                $"/DestinationImageFile:{workingWim}", workingCompression, "/CheckIntegrity").ConfigureAwait(false);
            Stage("Mount", "正在挂载工作副本。取消将等待当前 DISM 操作完成。");
            session.Mounted = true;
            try
            {
                await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1", $"/MountDir:{mount}",
                    "/CheckIntegrity").ConfigureAwait(false);
            }
            catch (DismCommandException error) when (execution.OptimizedMount && error.ExitCode == 87 &&
                Regex.IsMatch(error.Output, @"optimize\s+option\s+is\s+unknown", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                tracking.Skip("mount:lazy-check", "Mount", "验证优化挂载",
                    "此维护工具不支持优化挂载。正在以完整性检查正常重试。");
                await DiscardMountAsync(dism, mount).ConfigureAwait(false);
                session.Mounted = false;
                token.ThrowIfCancellationRequested();
                dism.DisableOptimizedMount();
                session.UsedOptimizedMount = false;
                MountOptimizationCache.TryRecordUnavailable(recipe.WorkspacePath, recipe.DismPath, error.ExitCode, session.Warn);
                session.Mounted = true;
                await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1", $"/MountDir:{mount}",
                    "/CheckIntegrity").ConfigureAwait(false);
            }
            if (session.UsedOptimizedMount)
            {
                try
                {
                    prefetchedFeatures = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
                }
                catch (DismCommandException error) when (error.ExitCode == 4350)
                {
                    tracking.Skip("mount:lazy-check", "Mount", "验证优化挂载",
                        "此主机拒绝了延迟映像访问（DISM 4350）。在进行任何映像更改前正在正常重试挂载。");
                    await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
                    session.Mounted = false;
                    token.ThrowIfCancellationRequested();
                    dism.DisableOptimizedMount();
                    session.UsedOptimizedMount = false;
                    MountOptimizationCache.TryRecordUnavailable(recipe.WorkspacePath, recipe.DismPath, error.ExitCode, session.Warn);
                    await tracking.RunAsync("mount:normal-retry", "Mount", "延迟挂载不可用；以完整性检查正常挂载",
                        async () =>
                        {
                            session.Mounted = true;
                            await dism.RunAsync(token, "/Mount-Image", $"/ImageFile:{workingWim}", "/Index:1",
                                $"/MountDir:{mount}", "/CheckIntegrity").ConfigureAwait(false);
                        }).ConfigureAwait(false);
                    prefetchedFeatures = DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false));
                }
            }
            IReadOnlyList<ImageInventoryItem> inventory = [];
            if (recipe.ExpertActions.Length > 0)
            {
                Stage("Inventory", "正在根据实际挂载映像重新验证专家操作。");
                inventory = await ImageInventory.ReadAsync(dism, imageOperations, mount, token).ConfigureAwait(false);
                ImageInventory.ValidateActions(recipe.ExpertActions, inventory);
            }
            if (servicingActions.Length > 0)
            {
                Stage("Custom servicing", "正在应用所选程序包、应用、能力与功能操作。");
                foreach (var action in servicingActions)
                    tracking.Plan($"expert:{action.Kind}:{action.Identity}", "Custom servicing", $"{action.Mode} {action.Kind}：{action.Identity}");
                foreach (var action in servicingActions)
                {
                    if (action.Kind == ImageItemKind.Capability)
                    {
                        await RemoveCapabilitiesAsync(session, [action.Identity], false, "Custom servicing", token, expert: true).ConfigureAwait(false);
                        continue;
                    }
                    await tracking.RunAsync($"expert:{action.Kind}:{action.Identity}", "Custom servicing",
                        $"{action.Mode} {action.Kind}：{action.Identity}",
                        () => ApplyDismActionAsync(dism, mount, action, token)).ConfigureAwait(false);
                }
            }
            Stage("Remove apps", "正在按确切标识移除所选预配应用。");
            var appOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.App && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            IReadOnlyList<ProvisionedApp> apps = appOptions.Length > 0 ?
                DismClient.ParseApps(await dism.RunAsync(token, $"/Image:{mount}", "/Get-ProvisionedAppxPackages").ConfigureAwait(false)) : [];
            var appsToRemove = apps.Where(app => appOptions.Any(option => CleanupCatalog.MatchesApp(option, app)))
                .DistinctBy(app => app.PackageName).ToArray();
            foreach (var app in appsToRemove) tracking.Plan("app:" + app.PackageName, "Remove apps", $"移除 {app.DisplayName} ({app.PackageName})");
            foreach (var option in appOptions)
            {
                var matching = apps.Where(a => CleanupCatalog.MatchesApp(option, a)).ToArray();
                if (matching.Length == 0) tracking.Skip("missing-app:" + option.Id, "Remove apps", option.Title, "Not present in this image");
            }
            foreach (var app in appsToRemove)
                await tracking.RunAsync("app:" + app.PackageName, "Remove apps", $"移除 {app.DisplayName} ({app.PackageName})",
                    () => dism.RunAsync(token, $"/Image:{mount}", "/Remove-ProvisionedAppxPackage", $"/PackageName:{app.PackageName}")).ConfigureAwait(false);
            Stage("Optional features", "正在检测实际功能与能力；不可用的选择会报告，不会猜测。");
            var featureOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.Feature && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            var capabilityOptions = CleanupCatalog.Options.Where(o => o.Kind == OptionKind.Capability && recipe.SelectedOptions.Contains(o.Id)).ToArray();
            IReadOnlyDictionary<string, string> features = featureOptions.Length > 0 ?
                prefetchedFeatures is not null && servicingActions.Length == 0 ? prefetchedFeatures :
                    DismClient.ParseFeatures(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Features").ConfigureAwait(false)) :
                new Dictionary<string, string>();
            IReadOnlyList<ImageInventoryItem> capabilities = capabilityOptions.Length > 0 ?
                ImageInventory.ParseIdentityStates(await dism.RunAsync(token, $"/Image:{mount}", "/Get-Capabilities").ConfigureAwait(false),
                    "Capability Identity", ImageItemKind.Capability) : [];
            var featureNames = featureOptions.SelectMany(o => o.Targets).Where(features.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var removalCapabilities = capabilities.Where(c => c.Detail.Equals("Installed", StringComparison.OrdinalIgnoreCase) &&
                capabilityOptions.Any(o => o.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                             c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))))
                .DistinctBy(c => c.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var pending in capabilities.Where(c =>
                         new RemovalVerification(c.Identity, c.Detail, DismClient.ClassifyCapabilityState(c.Detail)).Deferred &&
                         capabilityOptions.Any(o => o.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                                    c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase)))))
            {
                var outcome = new RemovalVerification(pending.Identity, pending.Detail, RemovalProof.Pending);
                await tracking.RunAsync("capability:" + pending.Identity, "Optional features",
                    $"Capability removal already deferred: {pending.Identity}", () => Task.CompletedTask,
                    () => Sentence(outcome.Describe())).ConfigureAwait(false);
                RecordCapabilities(session, [outcome]);
            }
            foreach (var name in featureNames) tracking.Plan("feature:" + name, "Optional features", $"移除可选功能：{name}");
            foreach (var capability in removalCapabilities)
                tracking.Plan("capability:" + capability.Identity, "Optional features", $"请求移除能力：{capability.Identity}");
            foreach (var option in featureOptions)
            {
                var matching = option.Targets.Where(features.ContainsKey).ToArray();
                if (matching.Length == 0) tracking.Skip("missing-feature:" + option.Id, "Optional features", option.Title, "Not present in this image");
            }
            var activeFeatures = featureNames.Where(feature => !features[feature].Equals("Disabled with Payload Removed", StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var feature in featureNames.Except(activeFeatures, StringComparer.OrdinalIgnoreCase))
                tracking.Skip("feature:" + feature, "Optional features", feature, "Payload already removed");
            if (recipe.TurboMode && activeFeatures.Length > 1)
            {
                Log($"Turbo feature batch: {string.Join(", ", activeFeatures)}");
                FeatureBatchResult? featureBatch = null;
                await tracking.RunBatchAsync(activeFeatures.Select(feature => ("feature:" + feature, $"Remove optional feature: {feature}")).ToArray(),
                    "Optional features", async () =>
                    {
                        featureBatch = await dism.DisableFeaturesWithStatesAsync(mount, activeFeatures, token).ConfigureAwait(false);
                    },
                    detailFor: id => featureBatch?.Items.FirstOrDefault(item => string.Equals("feature:" + item.Name, id, StringComparison.Ordinal))
                        is { } verified ? Sentence(verified.Describe()) : null).ConfigureAwait(false);
                // Plain "Disabled" is reported as such, never as "payload removed": DISM keeps the payload of some features.
                foreach (var verified in featureBatch!.Items) Log($"Feature {verified.Name}: {verified.Describe()}.");
                session.Features.AddRange(featureBatch.Items);
            }
            else
                foreach (var feature in activeFeatures)
                    await tracking.RunAsync("feature:" + feature, "Optional features", $"Remove optional feature: {feature}",
                        () => dism.RunAsync(token, $"/Image:{mount}", "/Disable-Feature", $"/FeatureName:{feature}", "/Remove", "/NoRestart")).ConfigureAwait(false);
            foreach (var option in capabilityOptions)
            {
                if (!session.Capabilities.Any(c => option.Targets.Any(p => c.Name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                                         c.Name.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))) &&
                    !removalCapabilities.Any(c => option.Targets.Any(p => c.Identity.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase) ||
                                                                         c.Identity.StartsWith(p + "~", StringComparison.OrdinalIgnoreCase))))
                    tracking.Skip("missing-capability:" + option.Id, "Optional features", option.Title, "No matching installed capability");
            }
            await RemoveCapabilitiesAsync(session, removalCapabilities.Select(c => c.Identity).ToArray(),
                recipe.TurboMode, "Optional features", token).ConfigureAwait(false);
            WarnDeferredCapabilities(session);
            // DISM refuses cleanup while operations are pending, and .NET 3.5 leaves some until first boot.
            if (recipe.SelectedOptions.Contains("cleanup") || recipe.Flavor == BuildFlavor.Core)
            {
                Stage("Component cleanup", recipe.Flavor == BuildFlavor.Core ?
                    "Cleaning and resetting the component base before Core payload pruning." : "Cleaning superseded components without ResetBase.");
                await CleanComponentsAsync(session, token).ConfigureAwait(false);
            }
            if (recipe.EnableNetFx3)
            {
                Stage(".NET 3.5", "正在从匹配源介质启用 .NET 3.5（不使用 Windows 更新）。");
                var sxs = Path.Combine(media.RootPath, @"sources\sxs");
                if (!Directory.Exists(sxs)) throw new DirectoryNotFoundException("The source has no sources\\sxs payload for .NET 3.5.");
                await dism.RunAsync(token, $"/Image:{mount}", "/Enable-Feature", "/FeatureName:NetFx3", "/All",
                    "/LimitAccess", $"/Source:{sxs}", "/NoRestart").ConfigureAwait(false);
            }
            Stage("Windows settings", "正在将所选注册表设置应用到离线映像。");
            await tracking.RunAsync("registry:settings", "Windows settings", "Apply and verify offline Windows settings",
                () =>
                {
                    registry.Apply(mount, recipe, Log);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(staging, "autounattend.xml"), UnattendBuilder.Create(image, recipe));
            Stage("Advanced payloads", "正在仅对离线副本应用原版构建器与专家资源包移除。");
            if (RemovesComponentPayloads(recipe)) await EnsureNoPendingOperationsAsync(session, token).ConfigureAwait(false);
            imageOperations.RemoveOriginalFiles(mount, image.Architecture, OriginalFileOptions(session), Log, recipe.TurboMode);
            if (recipe.Flavor == BuildFlavor.Core)
            {
                Stage("Core pruning", "正在修剪 Core 组件资源包与恢复文件。");
                imageOperations.TrimCore(mount, image.Architecture, Log, token, recipe.TurboMode);
            }
            imageOperations.ApplyNativeActions(mount, recipe.ExpertActions, Log, recipe.TurboMode);
            if (recipe.RemoveRedundantData)
            {
                Stage("Redundant data", "正在移除离线缓存、归档日志与崩溃报告。");
                session.Cleanup = imageOperations.CleanRedundantData(mount, Log, token);
                foreach (var item in session.Cleanup.Where(item => item.State == BuildOperationState.Skipped))
                    tracking.Skip("cleanup:" + item.Path, "Redundant data", $"Clean {item.Path}", item.Detail);
            }
            Stage("Commit", "正在提交已修改的工作映像。");
            await dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity").ConfigureAwait(false);
            session.Mounted = false;
            // The checkpoint is durable before any later stage can observe a cancellation request.
            await SaveCommittedCheckpointAsync(session, new SourceIdentity
            {
                Location = recipe.SourcePath, ImageIndex = recipe.ImageIndex, InstallFile = Path.GetFileName(sourceWim),
                InstallLength = sourceInstall.Length, InstallLastWriteUtc = new(sourceInstall.LastWriteTimeUtc, TimeSpan.Zero)
            }).ConfigureAwait(false);
            return await RunTailAsync(session, token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw await FailAsync(session, error).ConfigureAwait(false);
        }
    }
    public Task RecoverAsync(string jobFile, string dismPath, CancellationToken token) =>
        RecoverAsync(jobFile, dismPath, token, discardCommitted: false);

    /// <summary>
    /// Discards an interrupted job's mounted working copy and staged media. A job holding verified saved progress is
    /// refused unless <paramref name="discardCommitted"/> explicitly accepts losing it; Resume is the way to keep it.
    /// Jobs from before checkpoints existed are never adopted or deleted automatically.
    /// </summary>
    public async Task RecoverAsync(string jobFile, string dismPath, CancellationToken token, bool discardCommitted)
    {
        jobFile = PathSafety.Full(jobFile);
        var directory = Path.GetDirectoryName(jobFile)!;
        var leaf = Path.GetFileName(directory);
        PathSafety.RejectReparsePoints(jobFile);
        if (Path.GetFileName(jobFile) != "job.json" || !leaf.StartsWith("tiny11-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(leaf[7..], "N", out _))
            throw new InvalidDataException("请在 tiny11 作业目录内选择 job.json。");
        var state = JsonSerializer.Deserialize<JobState>(File.ReadAllText(jobFile), JsonDefaults.Options)
            ?? throw new InvalidDataException("无效的作业标记。");
        if (state.Id != leaf[7..] || state.Status == "completed") throw new InvalidDataException("这不是中断的作业。");
        PathSafety.RejectReparsePoints(Path.Combine(directory, "job.lock"));
        // An active build holds this lock, including across separate app instances.
        using var jobLock = new FileStream(Path.Combine(directory, "job.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Refuse before anything is touched: a checkpoint means a verified committed image that Resume can finish.
        var checkpointFile = Path.Combine(directory, JobCheckpoint.FileName);
        PathSafety.RejectReparsePoints(checkpointFile);
        var checkpointed = File.Exists(checkpointFile);
        if (checkpointed && !discardCommitted)
            throw new CheckpointedJobException("此作业持有已验证的已保存进度（已提交的 Windows 映像及其暂存安装文件）。 " +
                "Use Resume to finish it without repeating that work. Recovery would permanently delete the saved image, so it needs explicit confirmation.");
        if (OperatingSystem.IsWindows()) OfflineHive.RecoverJob(directory);
        var mount = Path.Combine(directory, "mount");
        var dism = new DismClient(dismPath, runner);
        if (await IsMountedAsync(dism, mount).ConfigureAwait(false))
            await dism.RunAsync(token, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        // The checkpoint leaves its resumable name BEFORE the data it describes is deleted, so no resume can see a half-deleted job.
        if (checkpointed) CheckpointStore.Retire(directory, "checkpoint.discarded.json");
        foreach (var name in new[] { "media", "mount", "scratch", "setup", "compress" })
            PathSafety.DeleteOwnedDirectory(Path.Combine(directory, name), directory);
        DeleteWorkingWim(directory);
        File.WriteAllText(jobFile, JsonSerializer.Serialize(state with { Status = "recovered", Stage = "Discarded working copy" }, JsonDefaults.Options));
    }

    private static async Task ApplyDismActionAsync(DismClient dism, string mount, ExpertAction action, CancellationToken token)
    {
        var args = action.Kind switch
        {
            ImageItemKind.ProvisionedApp => new[] { "/Remove-ProvisionedAppxPackage", $"/PackageName:{action.Identity}" },
            ImageItemKind.Package => ["/Remove-Package", $"/PackageName:{action.Identity}", "/NoRestart"],
            ImageItemKind.Capability => ["/Remove-Capability", $"/CapabilityName:{action.Identity}", "/NoRestart"],
            ImageItemKind.Feature => action.Mode == ExpertActionMode.Remove ?
                ["/Disable-Feature", $"/FeatureName:{action.Identity}", "/Remove", "/NoRestart"] :
                ["/Disable-Feature", $"/FeatureName:{action.Identity}", "/NoRestart"],
            _ => throw new InvalidDataException("不是 DISM 操作。")
        };
        await dism.RunAsync(token, [$"/Image:{mount}", .. args]).ConfigureAwait(false);
    }

    /// <summary>Discards an owned mount without throwing: the caller records a failure as a secondary problem and demands recovery.</summary>
    private static async Task<Exception?> TryDiscardAsync(DismClient dism, string mount)
    {
        try
        {
            await DiscardMountAsync(dism, mount).ConfigureAwait(false);
            return null;
        }
        catch (Exception cleanup)
        {
            return cleanup;
        }
    }

    private static async Task DiscardMountAsync(DismClient dism, string mount)
    {
        if (await IsMountedAsync(dism, mount).ConfigureAwait(false))
            await dism.RunAsync(CancellationToken.None, "/Unmount-Image", $"/MountDir:{mount}", "/Discard").ConfigureAwait(false);
    }

    private static string DiscardFailureText(Exception discardFailure) =>
        $"DISCARD FAILED: {discardFailure.Message}. Use Recover workspace with this job.json; never use global DISM cleanup.";

    private static string DiscardText(Exception? discardFailure, bool resumable) => discardFailure is null ? "" : resumable ?
        $" Its mounted Setup image could not be discarded ({discardFailure.Message}). Resume tries to discard it again before it continues; " +
        "never use global DISM cleanup." :
        $" Recovery is required: its mounted image could not be discarded ({discardFailure.Message}). " +
        "Choose Recover workspace for this job; never use global DISM cleanup.";

    private static string SecondaryText(IReadOnlyList<Exception> secondary, Exception? discardFailure)
    {
        var diagnostics = secondary.Where(problem => !ReferenceEquals(problem, discardFailure)).Select(problem => problem.Message).ToArray();
        return diagnostics.Length == 0 ? "" : " Additional problems while recording the failure: " + string.Join("; ", diagnostics);
    }

    private static string Sentence(string text) => char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static async Task<bool> IsMountedAsync(DismClient dism, string mount)
    {
        var text = await dism.RunAsync(CancellationToken.None, "/Get-MountedWimInfo").ConfigureAwait(false);
        return Regex.Matches(text, @"(?m)^[ \t]*Mount Dir[ \t]*:[ \t]*(.+?)[ \t\r]*$")
            .Any(m => Path.GetFullPath(m.Groups[1].Value).TrimEnd('\\')
                .Equals(Path.GetFullPath(mount).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    private static void DeleteWorkingWim(string jobDirectory)
    {
        var file = Path.Combine(jobDirectory, "working.wim");
        if (File.Exists(file)) File.Delete(file);
    }
}
