using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using DistantWorlds2.Core;
using Xenko.Core.IO;
using Xenko.Core.Serialization.Contents;
using Xenko.Core.Storage;

namespace DistantWorlds2.AssetExtractor;

public static class Program
{
    private const string VfsRoot = "/dw2install";
    private const int BundleProgressLogInterval = 250;

    [Flags]
    private enum AssetKindFlags
    {
        None = 0,
        Dds = 1 << 0,
        Png = 1 << 1,
        Wav = 1 << 2,
        Fbx = 1 << 3,
        Misc = 1 << 4,
        All = Dds | Png | Wav | Fbx | Misc,
    }

    private enum FileHandlingMode
    {
        Unspecified,
        Overwrite,
        SkipExisting,
        // Transient, resolved away before extraction starts: the output folder is wiped, then fileMode
        // is downgraded to Overwrite (an empty folder makes Overwrite and SkipExisting equivalent anyway),
        // so nothing downstream of that resolution step ever needs to know this value existed.
        Recreate,
    }

    private enum InteractiveMode
    {
        // Default: skip install/output dir questions, ask only modules and asset types
        SelectModules,
        // Ask all questions: install dir, output dir, modules, asset types
        AskAll,
    }

    private static readonly (string Label, AssetKindFlags Flag)[] AssetTypeOptions =
    [
        ("Textures - DDS (.dds)", AssetKindFlags.Dds),
        ("Textures - PNG (.png, converted from DDS)", AssetKindFlags.Png),
        ("Sounds (.wav)", AssetKindFlags.Wav),
        ("Meshes (.fbx)", AssetKindFlags.Fbx),
        ("Everything else / unsupported (copied as-is)", AssetKindFlags.Misc),
    ];

    public static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        RunLogger.Initialize(ResolveLogPath());

        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += (_, resolveArgs) =>
            {
                var path = Path.Combine(AppContext.BaseDirectory, new AssemblyName(resolveArgs.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };

            RunLogger.Info("Distant Worlds 2 Asset Extractor");
            RunLogger.Info("Extracts and converts every asset from every bundle: textures -> .dds, sounds -> .wav,");
            RunLogger.Info("meshes -> .fbx, everything else copied as-is (shader source, data files, ...).");
            RunLogger.Info(string.Empty);

            var options = ParseArgs(args);
            string? installDir = options.InstallDir;
            string? outputDir = options.OutputDir;
            string? bundleFilter = options.BundleFilter;
            var fileMode = options.FileMode;
            var fileModeExplicitlySet = options.FileModeExplicitlySet;
            var interactiveMode = options.InteractiveMode;

            var settings = UserSettings.Load();
            TextureConverter.SetFfmpegTimeoutSeconds(settings.GetTextureFfmpegTimeoutSeconds());
            SoundConverter.SetFfmpegTimeoutSeconds(settings.GetSoundFfmpegTimeoutSeconds());
            // Resolve ffmpeg's path once, up front, rather than relying on lazy first-use discovery in
            // TextureConverter/SoundConverter — once conversions run concurrently on the worker pool below,
            // several threads could otherwise race through that discovery on the very first run.
            TextureConverter.FindFFmpeg();
            SoundConverter.FindFFmpeg();
            var maxParallelBundles = options.MaxParallelBundles ?? settings.GetMaxParallelBundles();
            var maxParallelConversions = options.MaxParallelConversions ?? settings.GetMaxParallelConversions();
            var verbose = options.Verbose;

            // Paths from CLI are always used directly (non-interactive mode).
            // If paths are not from CLI:
            //   - In AskAll mode, always prompt for them
            //   - In SelectModules mode (default), use saved paths if available; only prompt if missing
            bool pathsFromCli = installDir != null && outputDir != null;
            bool needsPathPrompts = !pathsFromCli &&
                (interactiveMode == InteractiveMode.AskAll ||
                 settings.InstallDir == null || settings.OutputDir == null);
            // We're in "pure CLI mode" (no user interaction) only if paths are all from CLI
            bool pureCLIMode = pathsFromCli;
            bool interactive = !pureCLIMode;

            if (installDir == null)
            {
                var savedInstallDir = settings.InstallDir;
                if (savedInstallDir != null && !File.Exists(Path.Combine(savedInstallDir, "DistantWorlds2.exe")))
                    savedInstallDir = null; // no longer a valid DW2 install, don't offer to reuse it

                if (needsPathPrompts)
                {
                    installDir = ConfirmOrPickFolder(savedInstallDir,
                        "Use your previously selected Distant Worlds 2 install folder?",
                        "Select your Distant Worlds 2 install folder (contains DistantWorlds2.exe)");
                }
                else
                {
                    // No prompts needed, use saved install dir
                    installDir = savedInstallDir;
                }
            }
            if (installDir == null)
            {
                RunLogger.Info("Cancelled.");
                return 1;
            }
            if (!File.Exists(Path.Combine(installDir, "DistantWorlds2.exe")))
            {
                RunLogger.Error($"'{installDir}' doesn't look like a Distant Worlds 2 install (DistantWorlds2.exe not found there).");
                return 1;
            }

            if (outputDir == null)
            {
                if (needsPathPrompts)
                {
                    outputDir = ConfirmOrPickFolder(settings.OutputDir,
                        "Use your previously selected output folder?",
                        "Select where extracted assets should be saved");
                }
                else
                {
                    // No prompts needed, use saved output dir
                    outputDir = settings.OutputDir;
                }
            }
            if (outputDir == null)
            {
                RunLogger.Info("Cancelled.");
                return 1;
            }

            // Determine file handling mode: either explicitly set via CLI, or prompt/default based on folder state.
            if (fileMode == FileHandlingMode.Unspecified)
            {
                var folderExists = Directory.Exists(outputDir);
                var folderIsEmpty = !folderExists || !Directory.EnumerateFileSystemEntries(outputDir).Any();

                if (!folderIsEmpty)
                {
                    // Folder exists and is not empty — must prompt (in interactive mode) or error (in non-interactive)
                    if (interactive)
                    {
                        var pickedMode = PickFileHandlingMode();
                        if (pickedMode is null)
                        {
                            RunLogger.Info("Cancelled.");
                            return 1;
                        }
                        fileMode = pickedMode.Value;
                    }
                    else
                    {
                        RunLogger.Error("Output folder is not empty. Specify -overwrite, -skip-existing, or -recreate to proceed in non-interactive mode.");
                        return 1;
                    }
                }
                else
                {
                    // Folder is empty or doesn't exist — default to Overwrite
                    fileMode = FileHandlingMode.Overwrite;
                }
            }

            var recreatedOutputDir = false;
            if (fileMode == FileHandlingMode.Recreate)
            {
                RunLogger.Info($"Deleting existing contents of '{outputDir}' (recreate from scratch)...");
                if (Directory.Exists(outputDir))
                {
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(outputDir))
                        {
                            // Preserve a .gitkeep placeholder at the output root — e.g. this repo's own
                            // Output/.gitkeep, which exists purely to keep the otherwise-empty, gitignored
                            // folder tracked in git. Recreate shouldn't delete it out from under the repo.
                            if (File.Exists(entry) && string.Equals(Path.GetFileName(entry), ".gitkeep", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (Directory.Exists(entry))
                                Directory.Delete(entry, recursive: true);
                            else
                                File.Delete(entry);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        RunLogger.Error($"Could not delete '{outputDir}': {ex.Message}");
                        RunLogger.Error("A file or folder inside it is likely open in Explorer, an editor, or another running instance of this tool. Close whatever has it open and try again.");
                        return 1;
                    }
                }
                else
                {
                    Directory.CreateDirectory(outputDir);
                }
                fileMode = FileHandlingMode.Overwrite;
                recreatedOutputDir = true;
            }

            var bundlesDir = Path.Combine(installDir, "data", "db", "bundles");
            if (!Directory.Exists(bundlesDir))
            {
                RunLogger.Error($"No bundles found at '{bundlesDir}'.");
                return 1;
            }

            var allBundles = BundleCatalog.ListBundleNames(bundlesDir).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
            if (allBundles.Count == 0)
            {
                RunLogger.Error($"No bundles found at '{bundlesDir}'.");
                return 1;
            }

            List<string> selectedBundles;
            if (bundleFilter != null)
            {
                selectedBundles = allBundles.Where(b => b.Contains(bundleFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (selectedBundles.Count == 0)
                {
                    RunLogger.Error($"No bundles match filter '{bundleFilter}'.");
                    return 1;
                }
            }
            else if (!interactive)
            {
                selectedBundles = allBundles;
            }
            else
            {
                var picked = PickBundles(allBundles, settings.LastSelectedBundles);
                if (picked == null || picked.Count == 0)
                {
                    RunLogger.Info("Cancelled.");
                    return 1;
                }
                selectedBundles = picked;
                settings.LastSelectedBundles = picked.Count == allBundles.Count ? null : picked;
            }

            AssetKindFlags assetTypes;
            if (!interactive)
            {
                assetTypes = AssetKindFlags.All;
            }
            else
            {
                var pickedTypes = PickAssetTypes(settings.LastSelectedAssetTypes);
                if (pickedTypes is null or AssetKindFlags.None)
                {
                    RunLogger.Info("Cancelled.");
                    return 1;
                }
                assetTypes = pickedTypes.Value;
                settings.LastSelectedAssetTypes = assetTypes == AssetKindFlags.All
                    ? null
                    : AssetTypeOptions.Where(o => assetTypes.HasFlag(o.Flag)).Select(o => o.Flag.ToString()).ToList();
            }

            if (interactive)
            {
                settings.InstallDir = installDir;
                settings.OutputDir = outputDir;
                settings.Save();
            }

            RunLogger.Info(string.Empty);
            RunLogger.Info($"Install:   {installDir}");
            RunLogger.Info($"Output:    {outputDir}");
            RunLogger.Info($"Bundles:   {selectedBundles.Count} of {allBundles.Count} selected");
            RunLogger.Info($"Types:     {string.Join(", ", AssetTypeOptions.Where(o => assetTypes.HasFlag(o.Flag)).Select(o => o.Label))}");
            RunLogger.Info($"Parallel:  {maxParallelBundles} bundle worker(s), {maxParallelConversions} conversion worker(s)");
            var fileModeLabel = fileMode == FileHandlingMode.Overwrite ? (recreatedOutputDir ? "overwrite (recreated from scratch)" : "overwrite")
                : fileMode == FileHandlingMode.SkipExisting ? "skip existing"
                : "unspecified";
            RunLogger.Info($"File Mode: {fileModeLabel}");
            if (verbose)
                RunLogger.Info("Verbose:   enabled");
            RunLogger.Info(string.Empty);

            return await Extract(installDir, outputDir, selectedBundles, assetTypes, maxParallelBundles, maxParallelConversions, verbose, fileMode);
        }
        catch (Exception ex)
        {
            // Last-resort catch-all: everything else in this file that can reasonably fail already
            // handles it locally (per-asset failures land in Stats.FailureDetails, not here). This exists
            // so a truly unexpected failure still exits cleanly with a one-line message instead of the
            // .NET runtime dumping a raw stack trace to the console.
            RunLogger.Error($"Unexpected error: {ex.Message}");
            return 1;
        }
        finally
        {
            RunLogger.Dispose();
        }
    }

    private sealed class CommandLineOptions
    {
        public string? InstallDir { get; init; }
        public string? OutputDir { get; init; }
        public string? BundleFilter { get; init; }
        public int? MaxParallelBundles { get; init; }
        public int? MaxParallelConversions { get; init; }
        public bool Verbose { get; init; }
        public FileHandlingMode FileMode { get; init; } = FileHandlingMode.Unspecified;
        public bool FileModeExplicitlySet { get; init; }
        public InteractiveMode InteractiveMode { get; init; } = InteractiveMode.SelectModules;
    }

    private static CommandLineOptions ParseArgs(string[] args)
    {
        var positional = new List<string>();
        int? maxParallelBundles = null;
        int? maxParallelConversions = null;
        var verbose = false;
        var fileMode = FileHandlingMode.Unspecified;
        var fileModeExplicitlySet = false;
        var interactiveMode = InteractiveMode.SelectModules;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "-h" or "-?" or "-help" or "--help")
            {
                PrintHelp();
                Environment.Exit(0);
            }

            if (arg is "-reset")
            {
                // Reset removes the settings file and uses AskAll mode
                UserSettings.Reset();
                interactiveMode = InteractiveMode.AskAll;
                continue;
            }

            if (arg is "-ask")
            {
                interactiveMode = InteractiveMode.AskAll;
                continue;
            }

            if (arg is "-j" or "--jobs" or "--max-parallelism" or "--max-parallel-bundles")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsed) || parsed <= 0)
                    throw new ArgumentException($"{arg} requires a positive integer value.");
                maxParallelBundles = parsed;
                continue;
            }

            if (arg is "--max-parallel-conversions")
            {
                if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsed) || parsed <= 0)
                    throw new ArgumentException($"{arg} requires a positive integer value.");
                maxParallelConversions = parsed;
                continue;
            }

            if (arg is "-v" or "--verbose")
            {
                verbose = true;
                continue;
            }

            if (arg is "-overwrite")
            {
                fileMode = FileHandlingMode.Overwrite;
                fileModeExplicitlySet = true;
                continue;
            }

            if (arg is "-skip-existing")
            {
                fileMode = FileHandlingMode.SkipExisting;
                fileModeExplicitlySet = true;
                continue;
            }

            if (arg is "-recreate")
            {
                fileMode = FileHandlingMode.Recreate;
                fileModeExplicitlySet = true;
                continue;
            }

            positional.Add(arg);
        }

        if (positional.Count != 0 && positional.Count != 2 && positional.Count != 3)
            throw new ArgumentException("Usage: dw2extract.exe [<installDir> <outputDir> [bundleNameFilter]] [-overwrite | -skip-existing | -recreate] [-j <count>] [--max-parallel-conversions <count>] [-v]");

        return new CommandLineOptions
        {
            InstallDir = positional.Count >= 2 ? positional[0] : null,
            OutputDir = positional.Count >= 2 ? positional[1] : null,
            BundleFilter = positional.Count == 3 ? positional[2] : null,
            MaxParallelBundles = maxParallelBundles,
            MaxParallelConversions = maxParallelConversions,
            Verbose = verbose,
            FileMode = fileMode,
            FileModeExplicitlySet = fileModeExplicitlySet,
            InteractiveMode = interactiveMode,
        };
    }

    private static void PrintHelp()
    {
        RunLogger.Info("Distant Worlds 2 Asset Extractor");
        RunLogger.Info(string.Empty);
        RunLogger.Info("Usage: dw2extract.exe [options]");
        RunLogger.Info(string.Empty);
        RunLogger.Info("Options:");
        RunLogger.Info("  [<installDir> <outputDir> [bundleNameFilter]]");
        RunLogger.Info("                           Specify paths and optional bundle filter directly (non-interactive).");
        RunLogger.Info(string.Empty);
        RunLogger.Info("  -ask                    Ask all interactive questions (install dir, output dir, modules, asset types).");
        RunLogger.Info("  -reset                  Clear saved settings and ask all questions.");
        RunLogger.Info(string.Empty);
        RunLogger.Info("  -overwrite              Overwrite existing files during extraction.");
        RunLogger.Info("  -skip-existing          Skip files that already exist (default with saved dirs).");
        RunLogger.Info("  -recreate               Delete output folder contents before extraction.");
        RunLogger.Info(string.Empty);
        RunLogger.Info("  -j <count>              Max parallel bundle extractions (default: auto-detected).");
        RunLogger.Info("  --max-parallel-conversions <count>");
        RunLogger.Info("                          Max parallel texture/sound/mesh conversions (default: processor count).");
        RunLogger.Info(string.Empty);
        RunLogger.Info("  -v, --verbose           Verbose logging output.");
        RunLogger.Info("  -h, -?, -help, --help   Show this help message.");
        RunLogger.Info(string.Empty);
        RunLogger.Info("Defaults:");
        RunLogger.Info("  When run without paths, uses saved install/output directories if available.");
        RunLogger.Info("  This skips the path selection prompts and goes straight to module/asset selection.");
    }

    private static string ResolveLogPath()
    {
        var envPath = Environment.GetEnvironmentVariable("DW2EXTRACT_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;
        return Path.Combine(AppContext.BaseDirectory, "dw2extractor.log");
    }

    private static string? PickFolderCore(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    // If savedPath is set, asks the user (Yes/No/Cancel) whether to reuse it before falling back to the
    // normal folder-browser dialog on "No". Returns null on Cancel or if the browser dialog is dismissed.
    private static string? ConfirmOrPickFolder(string? savedPath, string confirmMessage, string pickerDescription)
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            if (savedPath != null)
            {
                var choice = MessageBox.Show(
                    $"{confirmMessage}\n\n{savedPath}",
                    "Distant Worlds 2 Asset Extractor",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);

                if (choice == DialogResult.Yes)
                {
                    result = savedPath;
                    return;
                }
                if (choice == DialogResult.Cancel)
                    return;
                // No -> fall through to the folder browser below.
            }

            result = PickFolderCore(pickerDescription);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static List<string>? PickBundles(List<string> allBundles, List<string>? lastSelected)
    {
        List<string>? result = null;
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "Select bundles to extract",
                StartPosition = FormStartPosition.CenterScreen,
                Width = 420,
                Height = 520,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
            };

            var listBox = new CheckedListBox
            {
                Left = 10,
                Top = 10,
                Width = 380,
                Height = 400,
                CheckOnClick = true,
                IntegralHeight = false,
            };
            // null lastSelected means "all" (either never picked before, or last pick was everything) —
            // default every item checked in that case rather than treating it as an empty saved pick.
            foreach (var bundleName in allBundles)
            {
                var isChecked = lastSelected == null || lastSelected.Contains(bundleName, StringComparer.OrdinalIgnoreCase);
                listBox.Items.Add(bundleName, isChecked);
            }

            var selectAllButton = new Button { Text = "All", Left = 10, Top = 432, Width = 90, Height = 36 };
            selectAllButton.Click += (_, _) =>
            {
                for (int i = 0; i < listBox.Items.Count; i++)
                    listBox.SetItemChecked(i, true);
            };

            var selectNoneButton = new Button { Text = "Clear", Left = 105, Top = 432, Width = 90, Height = 36 };
            selectNoneButton.Click += (_, _) =>
            {
                for (int i = 0; i < listBox.Items.Count; i++)
                    listBox.SetItemChecked(i, false);
            };

            var okButton = new Button { Text = "OK", Left = 220, Top = 432, Width = 80, Height = 36, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 305, Top = 432, Width = 85, Height = 36, DialogResult = DialogResult.Cancel };

            form.Controls.Add(listBox);
            form.Controls.Add(selectAllButton);
            form.Controls.Add(selectNoneButton);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog() == DialogResult.OK)
                result = listBox.CheckedItems.Cast<string>().ToList();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static AssetKindFlags? PickAssetTypes(List<string>? lastSelected)
    {
        AssetKindFlags? result = null;
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "Select asset types to extract",
                StartPosition = FormStartPosition.CenterScreen,
                Width = 420,
                Height = 300,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
            };

            var listBox = new CheckedListBox
            {
                Left = 10,
                Top = 10,
                Width = 380,
                Height = 180,
                CheckOnClick = true,
                IntegralHeight = false,
            };
            // null lastSelected means "all" (either never picked before, or last pick was everything) —
            // default every item checked in that case rather than treating it as an empty saved pick.
            foreach (var (label, flag) in AssetTypeOptions)
            {
                var isChecked = lastSelected == null || lastSelected.Contains(flag.ToString(), StringComparer.OrdinalIgnoreCase);
                listBox.Items.Add(label, isChecked);
            }

            var selectAllButton = new Button { Text = "All", Left = 10, Top = 212, Width = 90, Height = 36 };
            selectAllButton.Click += (_, _) =>
            {
                for (int i = 0; i < listBox.Items.Count; i++)
                    listBox.SetItemChecked(i, true);
            };

            var selectNoneButton = new Button { Text = "Clear", Left = 105, Top = 212, Width = 90, Height = 36 };
            selectNoneButton.Click += (_, _) =>
            {
                for (int i = 0; i < listBox.Items.Count; i++)
                    listBox.SetItemChecked(i, false);
            };

            var okButton = new Button { Text = "OK", Left = 220, Top = 212, Width = 80, Height = 36, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 305, Top = 212, Width = 85, Height = 36, DialogResult = DialogResult.Cancel };

            form.Controls.Add(listBox);
            form.Controls.Add(selectAllButton);
            form.Controls.Add(selectNoneButton);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog() == DialogResult.OK)
            {
                var flags = AssetKindFlags.None;
                for (int i = 0; i < AssetTypeOptions.Length; i++)
                    if (listBox.GetItemChecked(i))
                        flags |= AssetTypeOptions[i].Flag;
                result = flags;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static FileHandlingMode? PickFileHandlingMode()
    {
        FileHandlingMode? result = null;
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                Text = "Output folder is not empty",
                StartPosition = FormStartPosition.CenterScreen,
                Width = 580,
                Height = 260,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog,
            };

            var label = new Label
            {
                Text = "The output folder already contains files. Choose how to handle them:",
                Left = 10,
                Top = 10,
                AutoSize = true,
            };

            // Skip existing is the least destructive choice, so it's the one pre-selected — same
            // "safe by default" intent as the old MessageBox defaulting its Enter key to Cancel.
            // AutoSize (rather than a fixed Width) avoids clipping the longer labels below — a fixed
            // pixel width that looks fine at design time can still clip at a different system font size
            // or DPI scale, since RadioButton doesn't wrap text within its bounds by default.
            var skipRadio = new RadioButton { Text = "Skip existing — leave already-extracted files alone", Left = 10, Top = 55, AutoSize = true, Checked = true };
            var overwriteRadio = new RadioButton { Text = "Overwrite existing — replace files that already exist", Left = 10, Top = 82, AutoSize = true };
            var recreateRadio = new RadioButton { Text = "Recreate from scratch — DELETES everything in the output folder first", Left = 10, Top = 109, AutoSize = true };

            var okButton = new Button { Text = "OK", Left = 380, Top = 155, Width = 80, Height = 36, DialogResult = DialogResult.OK };
            var cancelButton = new Button { Text = "Cancel", Left = 465, Top = 155, Width = 85, Height = 36, DialogResult = DialogResult.Cancel };

            form.Controls.Add(label);
            form.Controls.Add(skipRadio);
            form.Controls.Add(overwriteRadio);
            form.Controls.Add(recreateRadio);
            form.Controls.Add(okButton);
            form.Controls.Add(cancelButton);
            form.AcceptButton = okButton;
            form.CancelButton = cancelButton;

            if (form.ShowDialog() == DialogResult.OK)
            {
                result = recreateRadio.Checked ? FileHandlingMode.Recreate
                    : overwriteRadio.Checked ? FileHandlingMode.Overwrite
                    : FileHandlingMode.SkipExisting;
            }
            // Cancel -> result stays null
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static async Task<int> Extract(string installDir, string outputDir, List<string> selectedBundles, AssetKindFlags assetTypes, int maxParallelBundles, int maxParallelConversions, bool verbose, FileHandlingMode fileMode)
    {
        VirtualFileSystem.MountFileSystem(VfsRoot, installDir); // Xenko-side (bundle container plumbing)
        Stride.Core.IO.VirtualFileSystem.MountFileSystem(VfsRoot, installDir); // Stride-side (MeshExporter's ContentManager)

        // DW2 routinely lets a later-loaded bundle re-declare (override) an asset path that an earlier
        // one also declares — e.g. CoreContent and Human can both have their own "Ships/Human/..." entry,
        // and which one the game actually uses at runtime depends on load order we're not trying to
        // reconstruct here. This tool's job is just to get everything out where you can see it, so each
        // bundle's own declared asset list (its BundleDescription.Assets — literally what that bundle's
        // own file says it contains, with no dependency-merging) goes into its own subfolder, full stop.
        // No cross-bundle deduplication: the same path can legitimately appear once per bundle that
        // declares it.
        var stats = new Stats();
        var statsLock = new object();
        var activeBundles = 0;
        var maxObservedConcurrentBundles = 0;
        var totalStopwatch = Stopwatch.StartNew();
        // Shared across every bundle, not one pool per bundle — bundles already run up to maxParallelBundles
        // at once, so a per-bundle pool would multiply out to maxParallelBundles * maxParallelConversions
        // concurrent ffmpeg processes. This single pool keeps total conversion concurrency bounded regardless
        // of how many bundles happen to be running.
        using var conversionPool = new ConversionPool(maxParallelConversions);

        await Parallel.ForEachAsync(
            selectedBundles,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelBundles },
            async (bundleName, _) =>
            {
                var bundleStopwatch = Stopwatch.StartNew();
                var currentActive = Interlocked.Increment(ref activeBundles);
                UpdateMaxObserved(ref maxObservedConcurrentBundles, currentActive);
                if (verbose)
                    RunLogger.Info($"[bundle-start] {bundleName} active={currentActive}/{maxParallelBundles}");

                var bundleStats = await ExtractBundle(bundleName, outputDir, assetTypes, fileMode, conversionPool);
                lock (statsLock)
                {
                    stats.MergeFrom(bundleStats);
                }

                var remainingActive = Interlocked.Decrement(ref activeBundles);
                if (verbose)
                    RunLogger.Info($"[bundle-done]  {bundleName} elapsed={bundleStopwatch.Elapsed:hh\\:mm\\:ss} success={bundleStats.SuccessfulAssets} failed={bundleStats.Failures} active={remainingActive}/{maxParallelBundles}");
            });

        var successfulAssets = stats.Textures + stats.Sounds + stats.Meshes + stats.Raw;
        var attemptedAssets = successfulAssets + stats.Failures + stats.Skipped;

        RunLogger.Info(string.Empty);
        if (stats.Skipped > 0)
            RunLogger.Info($"Done. {successfulAssets} assets succeeded, {stats.Skipped} skipped, {stats.Failures} failed, {attemptedAssets} total attempted.");
        else
            RunLogger.Info($"Done. {successfulAssets} assets succeeded, {stats.Failures} failed, {attemptedAssets} total attempted.");
        RunLogger.Info($"Successful assets by type: {stats.Textures} textures, {stats.Sounds} sounds, {stats.Meshes} meshes, {stats.Raw} other.");
        RunLogger.Info($"Output files created: {stats.DdsFiles} dds, {stats.PngFiles} png, {stats.WavFiles} wav, {stats.FbxFiles} fbx, {stats.OtherFiles} other.");
        if (stats.SkippedFiles > 0)
            RunLogger.Info($"Output files skipped: {stats.SkippedFiles} (due to skip-existing mode).");
        RunLogger.Info($"Average time per successful asset: textures {stats.GetAverageMilliseconds(stats.TextureMilliseconds, stats.Textures):0.0} ms, sounds {stats.GetAverageMilliseconds(stats.SoundMilliseconds, stats.Sounds):0.0} ms, meshes {stats.GetAverageMilliseconds(stats.MeshMilliseconds, stats.Meshes):0.0} ms, other {stats.GetAverageMilliseconds(stats.RawMilliseconds, stats.Raw):0.0} ms.");
        if (verbose)
        {
            RunLogger.Info($"Parallel efficacy: max concurrent bundles observed {maxObservedConcurrentBundles}/{maxParallelBundles}, total elapsed {totalStopwatch.Elapsed:hh\\:mm\\:ss}.");
            RunLogger.Info($"Conversion efficacy: max concurrent conversions observed {conversionPool.MaxObservedActive}/{maxParallelConversions}.");
        }

        if (stats.Failures > 0)
        {
            RunLogger.Info(string.Empty);
            RunLogger.Yellow("Failures:");
            foreach (var failure in stats.FailureDetails)
                RunLogger.Yellow($"  - {failure}");
        }

        return 0;
    }

    private static void UpdateMaxObserved(ref int maxObserved, int candidate)
    {
        while (true)
        {
            var current = maxObserved;
            if (candidate <= current)
                return;
            if (Interlocked.CompareExchange(ref maxObserved, candidate, current) == current)
                return;
        }
    }

    private static bool ShouldWriteFile(string destPath, FileHandlingMode fileMode)
    {
        if (fileMode == FileHandlingMode.Overwrite)
            return true;
        // SkipExisting: only write if file doesn't exist
        return !File.Exists(destPath);
    }

    private static async Task<Stats> ExtractBundle(string bundleName, string outputDir, AssetKindFlags assetTypes, FileHandlingMode fileMode, ConversionPool conversionPool)
    {
        var stats = new Stats();

        List<string> ownAssetUrls;
        try
        {
            var bundleFileUrl = $"{VfsRoot}/data/db/bundles/{bundleName}.bundle";
            var description = Stride.Core.Storage.BundleOdbBackend.ReadBundleHeader(bundleFileUrl, out _);
            ownAssetUrls = description.Assets.Select(kv => kv.Key).ToList();
        }
        catch (Exception ex)
        {
            stats.Failures++;
            stats.FailureDetails.Add($"{bundleName}:(bundle header) -> {ex.Message}");
            RunLogger.Error($"  Failed to read bundle header for '{bundleName}': {ex.Message}");
            return stats;
        }

        ObjectDatabase odb;
        try
        {
            odb = new ObjectDatabase($"{VfsRoot}/data/db", "index", $"{VfsRoot}/data/db");
            await odb.LoadBundle(bundleName);
        }
        catch (Exception ex)
        {
            stats.Failures++;
            stats.FailureDetails.Add($"{bundleName}:(bundle load) -> {ex.Message}");
            RunLogger.Error($"  Failed to load bundle '{bundleName}': {ex.Message}");
            return stats;
        }

        var totalAssets = ownAssetUrls.Count(url => !url.EndsWith("_Data", StringComparison.Ordinal) && !url.EndsWith("/path", StringComparison.Ordinal));
        var processedAssets = 0;
        // Conversion (dds/png/wav/fbx encode) is pure file-in/file-out once the raw bytes are on disk, so it's
        // scheduled onto the shared ConversionPool instead of running inline — the loop below only has to wait
        // for the raw extraction itself (which does need the shared odb/fileProvider and can't be parallelized)
        // before moving on to the next asset. "processed" in the progress log below therefore means "read from
        // the bundle", not "fully converted" — conversions for earlier assets may still be finishing in the
        // background. Each conversion task returns its own Stats delta, merged into the bundle's stats only
        // after every one of them completes, so there's no concurrent writer to `stats` at any point.
        var pendingConversions = new List<Task<Stats>>();

        using (odb)
        {
            var fileProvider = new DatabaseFileProvider(odb);

            foreach (var url in ownAssetUrls)
            {
                if (url.EndsWith("_Data", StringComparison.Ordinal) || url.EndsWith("/path", StringComparison.Ordinal))
                    continue;

                await ExtractOne(odb, fileProvider, bundleName, url, outputDir, assetTypes, fileMode, stats, conversionPool, pendingConversions);
                processedAssets++;

                if (processedAssets == totalAssets || processedAssets % BundleProgressLogInterval == 0)
                    RunLogger.Info($"  [{bundleName}] {processedAssets}/{totalAssets} assets processed");
            }
        }

        foreach (var delta in await Task.WhenAll(pendingConversions))
            stats.MergeFrom(delta);

        return stats;
    }

    // Only the parts that need the shared, non-thread-safe per-bundle odb/fileProvider run here — probing,
    // classification, skip-existing checks, and BundleExtractor.ExtractRaw. Everything after the raw bytes
    // are on disk (DDS/PNG/WAV encode, FBX export) is pure file-in/file-out, so it's handed to the shared
    // ConversionPool instead of running inline, letting the next asset's raw extraction start immediately.
    private static async Task ExtractOne(ObjectDatabase odb, DatabaseFileProvider fileProvider, string bundleName, string url, string outputDir, AssetKindFlags assetTypes, FileHandlingMode fileMode, Stats stats, ConversionPool conversionPool, List<Task<Stats>> pendingConversions)
    {
        var destBase = Path.Combine(outputDir, bundleName, AssetUrlPaths.Sanitize(url));
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            using var probeStream = fileProvider.OpenStream(url, VirtualFileMode.Open, VirtualFileAccess.Read, VirtualFileShare.Read, StreamFlags.Seekable);
            var chunkType = AssetTypeIdentifier.TryGetChunkType(probeStream);
            var kind = AssetTypeIdentifier.Classify(chunkType);

            switch (kind)
            {
                case DW2AssetKind.Skeleton:
                    // Pulled in automatically by whichever Model references it — not a useful standalone export.
                    return;

                case DW2AssetKind.Texture:
                {
                    var wantDds = assetTypes.HasFlag(AssetKindFlags.Dds);
                    var wantPng = assetTypes.HasFlag(AssetKindFlags.Png);
                    // If FBX extraction is enabled, we need PNG files for material linking, even if PNG
                    // isn't globally selected for viewing. Only create PNGs for FBX-referenced textures
                    // (the MeshExporter will look for them), not all textures.
                    var fbxEnabled = assetTypes.HasFlag(AssetKindFlags.Fbx);

                    if (!wantDds && !wantPng && !fbxEnabled)
                        return;

                    var ddsPath = destBase + ".dds";
                    var pngPath = destBase + ".png";

                    if (fileMode == FileHandlingMode.SkipExisting)
                    {
                        var ddsExists = File.Exists(ddsPath);
                        var ddsNeeded = wantDds && !ddsExists;
                        // PNG is needed if: user wants it, OR FBX is enabled AND PNG doesn't exist yet
                        var pngNeeded = (wantPng || fbxEnabled) && !File.Exists(pngPath);

                        if (!ddsNeeded && !pngNeeded)
                        {
                            // Everything we need is already there.
                            stats.Skipped++;
                            if (wantDds) stats.SkippedFiles++;
                            if (wantPng) stats.SkippedFiles++;
                            return;
                        }

                        if (!ddsNeeded && pngNeeded && ddsExists)
                        {
                            // The DDS is already on disk and the PNG is the only thing missing —
                            // convert straight from it instead of re-extracting and re-decoding the raw
                            // bundle asset just to regenerate a DDS we already have.
                            pendingConversions.Add(conversionPool.Schedule(() =>
                                Task.FromResult(ConvertExistingDdsToPng(ddsPath, pngPath, wantDds, wantPng, fbxEnabled, url, startedAt))));
                            return;
                        }
                    }

                    var rawPath = destBase + ".raw";
                    if (!await BundleExtractor.ExtractRaw(odb, fileProvider, url, rawPath))
                        throw new IOException("extract failed");

                    pendingConversions.Add(conversionPool.Schedule(() =>
                        Task.FromResult(ConvertTexture(rawPath, ddsPath, pngPath, wantDds, wantPng, fbxEnabled, bundleName, url, startedAt))));
                    return;
                }

                case DW2AssetKind.Sound:
                {
                    if (!assetTypes.HasFlag(AssetKindFlags.Wav))
                        return;

                    // Check if output already exists in skip mode
                    var wavPath = destBase + ".wav";
                    if (fileMode == FileHandlingMode.SkipExisting && File.Exists(wavPath))
                    {
                        stats.Skipped++;
                        stats.SkippedFiles++;
                        return;
                    }

                    var rawPath = destBase + ".raw";
                    if (!await BundleExtractor.ExtractRaw(odb, fileProvider, url, rawPath))
                        throw new IOException("extract failed");

                    pendingConversions.Add(conversionPool.Schedule(() =>
                        Task.FromResult(ConvertSound(rawPath, wavPath, bundleName, url, startedAt))));
                    return;
                }

                case DW2AssetKind.Model:
                {
                    if (!assetTypes.HasFlag(AssetKindFlags.Fbx))
                        return;

                    // Check if output already exists in skip mode
                    var fbxPath = destBase + ".fbx";
                    if (fileMode == FileHandlingMode.SkipExisting && File.Exists(fbxPath))
                    {
                        stats.Skipped++;
                        stats.SkippedFiles++;
                        return;
                    }

                    // MeshExporter.ExportToFbx builds its own independent ObjectDatabase/DatabaseFileProvider
                    // internally — it never touches this bundle's shared odb/fileProvider — so there's no
                    // "prepare" step needed here at all beyond the skip-existing check above.
                    pendingConversions.Add(conversionPool.Schedule(() => ConvertModel(bundleName, url, fbxPath, outputDir, startedAt)));
                    return;
                }

                case DW2AssetKind.RawOrUnknown:
                default:
                    if (!assetTypes.HasFlag(AssetKindFlags.Misc))
                        return;

                    // Check if output already exists in skip mode
                    if (fileMode == FileHandlingMode.SkipExisting && (File.Exists(destBase) || File.Exists(destBase + "_Data")))
                    {
                        stats.Skipped++;
                        var count = 0;
                        if (File.Exists(destBase))
                            count++;
                        if (File.Exists(destBase + "_Data"))
                            count++;
                        stats.SkippedFiles += count;
                        return;
                    }

                    // The raw copy IS the entire operation for this kind — nothing to hand off to the
                    // conversion pool.
                    if (!await BundleExtractor.ExtractRaw(odb, fileProvider, url, destBase))
                        throw new IOException("extract failed");
                    stats.Raw++;
                    stats.OtherFiles += CountRawOutputFiles(destBase);
                    stats.RawMilliseconds += Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
                    return;
            }
        }
        catch (Exception ex)
        {
            stats.Failures++;
            stats.FailureDetails.Add($"{bundleName}:{url} -> {ex.Message}");
            RunLogger.Error($"  FAILED {url}: {ex.Message}");
        }
    }

    // The DDS is already on disk (skip-existing shortcut) — only the PNG needs to be produced.
    private static Stats ConvertExistingDdsToPng(string ddsPath, string pngPath, bool wantDds, bool wantPng, bool fbxEnabled, string url, long startedAt)
    {
        var stats = new Stats();
        try
        {
            // Create PNG if user wants it OR if FBX is enabled (needs PNG for materials)
            if (wantPng || fbxEnabled)
            {
                TextureConverter.DdsToPng(ddsPath, pngPath);
                stats.Textures++;
                stats.PngFiles++;
                if (wantDds)
                    stats.SkippedFiles++;  // DDS already existed, wasn't recreated
            }
            else if (wantDds)
            {
                // Only DDS is wanted, it already exists, PNG is not wanted and FBX is not enabled — this is a complete skip
                stats.Skipped++;
                stats.SkippedFiles++;
            }
            stats.TextureMilliseconds += Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            RunLogger.Warn($"  (no PNG for {url}: {ex.Message})");
            stats.Skipped++;
            if (wantDds) stats.SkippedFiles++;
        }
        return stats;
    }

    private static Stats ConvertTexture(string rawPath, string ddsPath, string pngPath, bool wantDds, bool wantPng, bool fbxEnabled, string bundleName, string url, long startedAt)
    {
        var stats = new Stats();
        try
        {
            TextureConverter.XenkoToDds(rawPath, ddsPath);
            CleanupRaw(rawPath);
            File.Delete(ddsPath + ".refs");

            var pngOk = false;
            // Create PNG if user wants it OR if FBX is enabled (needs PNG for materials)
            if (wantPng || fbxEnabled)
            {
                try
                {
                    TextureConverter.DdsToPng(ddsPath, pngPath);
                    pngOk = true;
                }
                catch (Exception ex)
                {
                    // Some DDS variants (e.g. certain cubemap/array layouts) may not have a PNG-savable
                    // path in Xenko's Image.Save — the .dds itself already succeeded, so don't fail the
                    // whole asset over a missing PNG. Since PNG conversion failed, keep the .dds around
                    // regardless of whether the user asked for it, so the asset isn't lost entirely.
                    RunLogger.Warn($"  (no PNG for {url}: {ex.Message})");
                }
            }

            if (!wantDds && pngOk)
                File.Delete(ddsPath);

            stats.Textures++;
            if (wantDds || !pngOk)
                stats.DdsFiles++;
            if (pngOk)
                stats.PngFiles++;
            stats.TextureMilliseconds += Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            stats.Failures++;
            stats.FailureDetails.Add($"{bundleName}:{url} -> {ex.Message}");
            RunLogger.Error($"  FAILED {url}: {ex.Message}");
        }
        return stats;
    }

    private static Stats ConvertSound(string rawPath, string wavPath, string bundleName, string url, long startedAt)
    {
        var stats = new Stats();
        try
        {
            SoundConverter.XenkoToSoundfile(rawPath, wavPath);
            CleanupRaw(rawPath);
            stats.Sounds++;
            stats.WavFiles++;
            stats.SoundMilliseconds += Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            stats.Failures++;
            stats.FailureDetails.Add($"{bundleName}:{url} -> {ex.Message}");
            RunLogger.Error($"  FAILED {url}: {ex.Message}");
        }
        return stats;
    }

    private static async Task<Stats> ConvertModel(string bundleName, string url, string fbxPath, string outputDir, long startedAt)
    {
        var stats = new Stats();
        try
        {
            // ExportToFbx returns 0 on success, 1 on failure (e.g. model load failure) — previously this
            // return value was discarded here, so a failed export was logged as an error but still counted
            // as a successful mesh in the stats. Throwing on non-zero routes it into the normal failure path.
            var result = await MeshExporter.ExportToFbx(bundleName, url, fbxPath, outputDir, VfsRoot);
            if (result != 0)
                throw new InvalidOperationException($"MeshExporter.ExportToFbx failed (exit code {result})");

            stats.Meshes++;
            stats.FbxFiles++;
            stats.MeshMilliseconds += Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            stats.Failures++;
            stats.FailureDetails.Add($"{bundleName}:{url} -> {ex.Message}");
            RunLogger.Error($"  FAILED {url}: {ex.Message}");
        }
        return stats;
    }

    private static void CleanupRaw(string rawPath)
    {
        File.Delete(rawPath);
        if (File.Exists(rawPath + "_Data"))
            File.Delete(rawPath + "_Data");
    }

    private static int CountRawOutputFiles(string destBase)
    {
        var count = 0;
        if (File.Exists(destBase))
            count++;
        if (File.Exists(destBase + "_Data"))
            count++;
        return count;
    }

    // Bounded, process-wide pool for post-extraction conversion work (DDS/PNG/WAV encode, FBX export),
    // shared across every bundle rather than one pool per bundle — bundles already run concurrently via
    // Parallel.ForEachAsync in Extract(), so a per-bundle pool would multiply out to
    // maxParallelBundles * maxParallelConversions concurrent ffmpeg processes. A single shared pool keeps
    // total conversion concurrency bounded regardless of how many bundles are active at once.
    private sealed class ConversionPool : IDisposable
    {
        private readonly SemaphoreSlim semaphore;
        private int activeCount;
        private int maxObservedActive;

        public ConversionPool(int maxDegreeOfParallelism)
        {
            semaphore = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
        }

        public int MaxObservedActive => maxObservedActive;

        public Task<Stats> Schedule(Func<Task<Stats>> convert)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                var current = Interlocked.Increment(ref activeCount);
                UpdateMaxObserved(ref maxObservedActive, current);
                try
                {
                    return await convert();
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                    semaphore.Release();
                }
            });
        }

        public void Dispose() => semaphore.Dispose();
    }

    private class Stats
    {
        public int Textures, Sounds, Meshes, Raw, Failures, Skipped;
        public int DdsFiles, PngFiles, WavFiles, FbxFiles, OtherFiles, SkippedFiles;
        public double TextureMilliseconds, SoundMilliseconds, MeshMilliseconds, RawMilliseconds;
        public List<string> FailureDetails { get; } = [];
        public int SuccessfulAssets => Textures + Sounds + Meshes + Raw;

        public void MergeFrom(Stats other)
        {
            Textures += other.Textures;
            Sounds += other.Sounds;
            Meshes += other.Meshes;
            Raw += other.Raw;
            Failures += other.Failures;
            Skipped += other.Skipped;
            DdsFiles += other.DdsFiles;
            PngFiles += other.PngFiles;
            WavFiles += other.WavFiles;
            FbxFiles += other.FbxFiles;
            OtherFiles += other.OtherFiles;
            SkippedFiles += other.SkippedFiles;
            TextureMilliseconds += other.TextureMilliseconds;
            SoundMilliseconds += other.SoundMilliseconds;
            MeshMilliseconds += other.MeshMilliseconds;
            RawMilliseconds += other.RawMilliseconds;
            FailureDetails.AddRange(other.FailureDetails);
        }

        public double GetAverageMilliseconds(double totalMilliseconds, int count)
            => count == 0 ? 0 : totalMilliseconds / count;
    }
}
