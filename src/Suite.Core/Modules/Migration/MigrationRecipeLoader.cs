using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration.Detection;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>Thrown when a recipe JSON document is structurally or semantically invalid (fail-closed).</summary>
public sealed class RecipeValidationException : Exception
{
    public RecipeValidationException(string message) : base(message) { }
}

/// <summary>
/// Strict, fail-closed loader/validator for <see cref="MigrationRecipe"/> JSON (decision §"strict loader").
/// Unlike the tolerant <c>ManifestLoader</c>, this REJECTS:
/// <list type="bullet">
/// <item>any unknown top-level/nested field (no silent drop — a typo'd or smuggled field fails the whole recipe);</item>
/// <item>any unknown enum value (<c>knownFolder</c>, <c>portabilityClass</c>, <c>restore.strategy</c>, <c>restore.phase</c>);</item>
/// <item>an unsupported <c>schemaVersion</c>, a missing required field, or an empty id.</item>
/// </list>
/// Because recipes can ship from L4 user overrides (decision §"4 katman"), a strict validator is the line
/// that keeps a malformed/hostile recipe from ever reaching the resolver.
/// </summary>
public static class MigrationRecipeLoader
{
    /// <summary>The oldest recipe schema this loader accepts (v1: no <c>install</c> block).</summary>
    public const int MinSupportedSchemaVersion = 1;

    /// <summary>The newest recipe schema this loader accepts (v3: detection join keys + honest restore tier/meta).</summary>
    public const int MaxSupportedSchemaVersion = 3;

    /// <summary>The schema version at which the optional <c>install</c> block becomes a recognized root field.</summary>
    private const int InstallBlockSchemaVersion = 2;

    private const int V3SchemaVersion = 3;

    /// <summary>Retained for source compatibility — the lowest version this loader supports.</summary>
    public const int SupportedSchemaVersion = MinSupportedSchemaVersion;

    /// <summary>Load + validate a single recipe from a JSON string. Throws <see cref="RecipeValidationException"/>.</summary>
    public static MigrationRecipe Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new RecipeValidationException($"recipe is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new RecipeValidationException("recipe root must be a JSON object");

            // The schema version is read FIRST because it decides whether `install` is a recognized root field:
            // v1 rejects it as an unknown field (the built-in seeds are v1 and must keep loading), v2 allows it.
            int schemaVersion = RequireInt(root, "schemaVersion");
            if (schemaVersion < MinSupportedSchemaVersion || schemaVersion > MaxSupportedSchemaVersion)
                throw new RecipeValidationException(
                    $"unsupported schemaVersion {schemaVersion} (expected {MinSupportedSchemaVersion}..{MaxSupportedSchemaVersion})");

            // v1: the original allowed-root set (install is REJECTED as an unknown field). v2: install is allowed.
            // v3: additive exact set for detection join keys, restoreTier, migrationMeta, and upstream provenance.
            // Building the set from the version keeps the strict "reject unknown field" guarantee version-exact —
            // a v1 recipe smuggling `install` or a v2 recipe smuggling `migrationMeta` still fails closed.
            string[] v1Root =
                ["schemaVersion", "id", "displayName", "category", "detect", "items", "exclude", "secretRule", "portabilityClass", "restore"];
            string[] allowedRootFields = schemaVersion switch
            {
                1 => v1Root,
                2 => [.. v1Root, "install"],
                3 => [.. v1Root, "install", "wingetId", "productCode", "upgradeCode", "packageFamilyName",
                    "installPathHint", "restoreTier", "migrationMeta", "catalogTier", "upstreamDataLicense"],
                _ => v1Root,
            };
            RejectUnknownFields(root, "(root)", allowedRootFields);

            string id = RequireNonEmptyString(root, "id");
            ValidateId(id);
            string displayName = RequireNonEmptyString(root, "displayName");
            string category = OptionalString(root, "category") ?? string.Empty;

            RecipeDetect detect = ParseDetect(RequireObject(root, "detect"), schemaVersion);
            IReadOnlyList<RecipeItem> items = ParseItems(RequireArray(root, "items"), id, schemaVersion);
            IReadOnlyList<string> exclude = ParseStringArray(root, "exclude");
            string secretRule = OptionalString(root, "secretRule") ?? "global";
            if (!string.Equals(secretRule, "global", StringComparison.OrdinalIgnoreCase))
                throw new RecipeValidationException($"unknown secretRule '{secretRule}' (only 'global' is supported)");
            PortabilityClass portability = ParsePortability(RequireNonEmptyString(root, "portabilityClass"));
            RecipeRestore restore = ParseRestore(RequireObject(root, "restore"));

            // The optional v2 install block. Only ever present when schemaVersion >= 2 (else `install` would have
            // been rejected above). Set via object-initializer so MigrationRecipe's positional arity is unchanged.
            RecipeInstall? install = root.TryGetProperty("install", out JsonElement installEl)
                ? ParseInstall(installEl)
                : null;
            string? wingetId = OptionalString(root, "wingetId");
            if (!string.IsNullOrWhiteSpace(wingetId) && !InstallPlanner.IsValidWingetId(wingetId))
                throw new RecipeValidationException($"wingetId '{wingetId}' is not a valid winget package id");
            IReadOnlyList<string> productCode = ParseGuidArray(root, "productCode");
            IReadOnlyList<string> upgradeCode = ParseGuidArray(root, "upgradeCode");
            IReadOnlyList<string> packageFamilyName = ParseStringArray(root, "packageFamilyName");
            IReadOnlyList<string> installPathHint = ParseStringArray(root, "installPathHint");
            foreach (string hint in installPathHint)
                ValidateRelativePath(hint, "installPathHint[]");
            RestoreTier restoreTier = schemaVersion >= V3SchemaVersion
                ? ParseRestoreTier(RequireNonEmptyString(root, "restoreTier"))
                : RestoreTier.ConfigCopy;
            MigrationRecipeMeta? migrationMeta = root.TryGetProperty("migrationMeta", out JsonElement metaEl)
                ? ParseMigrationMeta(metaEl)
                : null;
            CatalogTier catalogTier = root.TryGetProperty("catalogTier", out _)
                ? ParseCatalogTier(RequireNonEmptyString(root, "catalogTier"))
                : CatalogTier.Trusted;
            UpstreamDataLicense upstreamDataLicense = root.TryGetProperty("upstreamDataLicense", out _)
                ? ParseUpstreamDataLicense(RequireNonEmptyString(root, "upstreamDataLicense"))
                : UpstreamDataLicense.Unknown;

            if (MustForceInventoryOnly(portability, detect, items))
                restoreTier = RestoreTier.InventoryOnly;

            return new MigrationRecipe(
                schemaVersion, id, displayName, category, detect, items, exclude, secretRule, portability, restore)
            {
                Install = install,
                WingetId = wingetId,
                ProductCode = productCode,
                UpgradeCode = upgradeCode,
                PackageFamilyName = packageFamilyName,
                InstallPathHint = installPathHint,
                RestoreTier = restoreTier,
                MigrationMeta = migrationMeta,
                CatalogTier = catalogTier,
                UpstreamDataLicense = upstreamDataLicense,
            };
        }
    }

    // The recipe id becomes a package SUBDIR (Entry.Target = "migration/{id}/{item}") that the backup runner
    // joins onto the package root. An id containing '/', '\', ':' or '..' would let a hostile recipe steer the
    // backup WRITE outside the package directory (the source sandbox guards the SOURCE, not the destination).
    // So the id must be a single, plain path segment: starts alphanumeric, then alphanumeric / '.' / '_' / '-'.
    // The built-in ids (git.config, anthropic.claude-code, microsoft.vscode, discord) all match.
    private static readonly Regex IdGrammar = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Reserved Windows device-name stems (case-insensitive). Containment already prevents an escape, but an id
    // that is a reserved name would make a problematic subdir on disk — reject it to future-proof the
    // community-recipe surface. No built-in recipe uses a reserved name.
    private static readonly IReadOnlySet<string> ReservedDeviceStems =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        };

    /// <summary>
    /// Reject any id that is not a single, plain path segment, or whose stem is a reserved Windows device name
    /// (decision §"recipe id is not path-validated"). This is the line that stops a package-escape via the id's
    /// presence in <c>Entry.Target</c>.
    /// </summary>
    private static void ValidateId(string id)
    {
        if (!IdGrammar.IsMatch(id))
            throw new RecipeValidationException(
                $"recipe id '{id}' is not a valid single segment (allowed: ^[A-Za-z0-9][A-Za-z0-9._-]*$)");

        // The stem is the portion before the first '.' (so "con.json" and "con" both trip the reserved check).
        string stem = id.Split('.', 2)[0];
        if (ReservedDeviceStems.Contains(stem))
            throw new RecipeValidationException($"recipe id '{id}' uses a reserved Windows device name ('{stem}')");
    }

    private static RecipeDetect ParseDetect(JsonElement el, int schemaVersion)
    {
        RejectUnknownFields(el, "detect", "knownFolder", "path", "exists");
        KnownFolder kf = ParseKnownFolder(RequireNonEmptyString(el, "knownFolder"), schemaVersion);
        string path = RequireNonEmptyString(el, "path");
        ValidateRelativePath(path, "detect.path");
        bool exists = el.TryGetProperty("exists", out JsonElement e) ? RequireBool(e, "detect.exists") : true;
        return new RecipeDetect(kf, path, exists);
    }

    private static IReadOnlyList<RecipeItem> ParseItems(JsonElement arr, string recipeId, int schemaVersion)
    {
        var list = new List<RecipeItem>();
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new RecipeValidationException("each item must be a JSON object");
            string[] allowedItemFields = schemaVersion >= V3SchemaVersion
                ? ["path", "include", "exclude", "kind", "libraryDetector", "launcherId", "exportKind",
                    "manualTodo", "requiresClosedProcesses", "verify", "expectedFormat"]
                : ["path", "include", "exclude"];
            RejectUnknownFields(el, "items[]", allowedItemFields);
            string path = RequireNonEmptyString(el, "path");
            ValidateRelativePath(path, "items[].path");
            RecipeItemKind kind = el.TryGetProperty("kind", out _)
                ? ParseItemKind(RequireNonEmptyString(el, "kind"))
                : RecipeItemKind.ProfilePath;
            ExportKind? exportKind = el.TryGetProperty("exportKind", out _)
                ? ParseExportKind(RequireNonEmptyString(el, "exportKind"))
                : null;
            string? libraryDetector = OptionalString(el, "libraryDetector");
            string? launcherId = OptionalString(el, "launcherId");
            IReadOnlyList<string> manualTodo = ParseStringArray(el, "manualTodo");
            if (kind == RecipeItemKind.ExportCmd && exportKind is null)
                throw new RecipeValidationException("items[] kind 'exportCmd' requires exportKind");
            if (kind == RecipeItemKind.MachineRoot
                && (string.IsNullOrWhiteSpace(libraryDetector) || string.IsNullOrWhiteSpace(launcherId)))
                throw new RecipeValidationException("items[] kind 'machineRoot' requires libraryDetector and launcherId");
            if (kind == RecipeItemKind.ManualTodo && manualTodo.Count == 0)
                throw new RecipeValidationException("items[] kind 'manualTodo' requires manualTodo");
            list.Add(new RecipeItem(path, ParseStringArray(el, "include"), ParseStringArray(el, "exclude"))
            {
                Kind = kind,
                LibraryDetector = libraryDetector,
                LauncherId = launcherId,
                ExportKind = exportKind,
                ManualTodo = manualTodo,
                RequiresClosedProcesses = ParseStringArray(el, "requiresClosedProcesses"),
                Verify = el.TryGetProperty("verify", out JsonElement verifyEl) ? ParseVerify(verifyEl) : null,
                ExpectedFormat = el.TryGetProperty("expectedFormat", out _)
                    ? ParseExpectedFormat(RequireNonEmptyString(el, "expectedFormat"))
                    : null,
            });
        }
        if (list.Count == 0)
            throw new RecipeValidationException($"recipe '{recipeId}' has no items");
        return list;
    }

    private static RecipeRestore ParseRestore(JsonElement el)
    {
        RejectUnknownFields(el, "restore", "strategy", "phase", "preconditions");
        RestoreStrategy strategy = ParseStrategy(RequireNonEmptyString(el, "strategy"));
        RestorePhase phase = ParsePhase(RequireNonEmptyString(el, "phase"));
        return new RecipeRestore(strategy, phase, ParseStringArray(el, "preconditions"));
    }

    /// <summary>
    /// Parse + validate the optional v2 <c>install</c> block fail-closed. The method must be a known value, and
    /// EXACTLY ONE locator must be present AND it must be the one the method requires (a winget method with an
    /// npm package, or two locators at once, is rejected). The winget id / npm name are checked through the SAME
    /// allow-lists the gated <see cref="InstallPlanner"/> applies to the executable argument, so a recipe can only
    /// ever describe the reviewed command — a path/leading-dash/whitespace id never gets past the loader.
    /// </summary>
    private static RecipeInstall ParseInstall(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new RecipeValidationException("field 'install' must be a JSON object");

        RejectUnknownFields(el, "install",
            "method", "wingetId", "npmPackage", "manualUrl", "requiresAdmin", "rebootExpected");

        RecipeInstallMethod method = ParseInstallMethod(RequireNonEmptyString(el, "method"));
        string? wingetId = OptionalString(el, "wingetId");
        string? npmPackage = OptionalString(el, "npmPackage");
        string? manualUrl = OptionalString(el, "manualUrl");
        bool requiresAdmin = el.TryGetProperty("requiresAdmin", out JsonElement ra) && RequireBool(ra, "install.requiresAdmin");
        bool rebootExpected = el.TryGetProperty("rebootExpected", out JsonElement re) && RequireBool(re, "install.rebootExpected");

        // EXACTLY ONE locator overall — a second locator (regardless of method) is a smuggled second command.
        int locators = (string.IsNullOrWhiteSpace(wingetId) ? 0 : 1)
                     + (string.IsNullOrWhiteSpace(npmPackage) ? 0 : 1)
                     + (string.IsNullOrWhiteSpace(manualUrl) ? 0 : 1);
        if (locators != 1)
            throw new RecipeValidationException(
                $"install must declare EXACTLY ONE locator (wingetId / npmPackage / manualUrl); found {locators}");

        // ... and that one locator must be the one the method requires, validated by the SAME guard as the planner.
        switch (method)
        {
            case RecipeInstallMethod.Winget:
                if (string.IsNullOrWhiteSpace(wingetId))
                    throw new RecipeValidationException("install method 'install-winget' requires a 'wingetId' locator");
                if (!InstallPlanner.IsValidWingetId(wingetId))
                    throw new RecipeValidationException($"install wingetId '{wingetId}' is not a valid winget package id");
                break;

            case RecipeInstallMethod.Npm:
                if (string.IsNullOrWhiteSpace(npmPackage))
                    throw new RecipeValidationException("install method 'install-npm' requires an 'npmPackage' locator");
                if (!InstallPlanner.IsValidNpmPackage(npmPackage))
                    throw new RecipeValidationException($"install npmPackage '{npmPackage}' is not a valid npm package name");
                break;

            case RecipeInstallMethod.UrlManual:
                if (string.IsNullOrWhiteSpace(manualUrl))
                    throw new RecipeValidationException("install method 'install-url-manual' requires a 'manualUrl' locator");
                break;
        }

        return new RecipeInstall(method, wingetId, npmPackage, manualUrl, requiresAdmin, rebootExpected);
    }

    private static RecipeItemVerify ParseVerify(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new RecipeValidationException("field 'verify' must be a JSON object");
        RejectUnknownFields(el, "verify", "exists", "maxSizeMB");
        int? maxSize = null;
        if (el.TryGetProperty("maxSizeMB", out JsonElement maxEl))
        {
            if (maxEl.ValueKind != JsonValueKind.Number || !maxEl.TryGetInt32(out int parsed) || parsed < 0)
                throw new RecipeValidationException("field 'verify.maxSizeMB' must be a non-negative integer");
            maxSize = parsed;
        }
        return new RecipeItemVerify(ParseStringArray(el, "exists"), maxSize);
    }

    private static MigrationRecipeMeta ParseMigrationMeta(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new RecipeValidationException("field 'migrationMeta' must be a JSON object");

        RejectUnknownFields(el, "migrationMeta", "uiWarning", "manualSteps", "manualTodo",
            "installerSource", "licenseSource", "requiresRelogin", "backedUpButNotRestored", "survivesOnOtherDrive");

        return new MigrationRecipeMeta(
            UiWarning: el.TryGetProperty("uiWarning", out JsonElement warnEl) ? ParseLocalizedText(warnEl) : null,
            ManualSteps: ParseStringArray(el, "manualSteps"),
            ManualTodo: ParseStringArray(el, "manualTodo"),
            InstallerSource: el.TryGetProperty("installerSource", out _) ? ParseInstallerSource(RequireNonEmptyString(el, "installerSource")) : null,
            LicenseSource: el.TryGetProperty("licenseSource", out _) ? ParseLicenseSource(RequireNonEmptyString(el, "licenseSource")) : null,
            RequiresRelogin: el.TryGetProperty("requiresRelogin", out JsonElement relogin) && RequireBool(relogin, "migrationMeta.requiresRelogin"),
            BackedUpButNotRestored: el.TryGetProperty("backedUpButNotRestored", out JsonElement backed) && RequireBool(backed, "migrationMeta.backedUpButNotRestored"),
            SurvivesOnOtherDrive: el.TryGetProperty("survivesOnOtherDrive", out JsonElement survives) && RequireBool(survives, "migrationMeta.survivesOnOtherDrive"));
    }

    private static LocalizedText ParseLocalizedText(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            string? s = el.GetString();
            return new LocalizedText(s, s);
        }

        if (el.ValueKind != JsonValueKind.Object)
            throw new RecipeValidationException("field 'uiWarning' must be a string or object");
        RejectUnknownFields(el, "uiWarning", "en", "tr");
        return new LocalizedText(OptionalString(el, "en"), OptionalString(el, "tr"));
    }

    // ---- enum parsing (fail-closed) --------------------------------------------------------

    private static KnownFolder ParseKnownFolder(string s, int schemaVersion)
    {
        string key = s.ToLowerInvariant();
        return key switch
        {
            "userprofile" => KnownFolder.UserProfile,
            "appdata" => KnownFolder.AppData,
            "localappdata" => KnownFolder.LocalAppData,
            "programdata" when schemaVersion >= V3SchemaVersion => KnownFolder.ProgramData,
            "programfiles" when schemaVersion >= V3SchemaVersion => KnownFolder.ProgramFiles,
            "programfilesx86" or "programfiles-x86" when schemaVersion >= V3SchemaVersion => KnownFolder.ProgramFilesX86,
            "windowsetc" or "windows-etc" when schemaVersion >= V3SchemaVersion => KnownFolder.WindowsEtc,
            _ => throw new RecipeValidationException($"unknown knownFolder '{s}'"),
        };
    }

    private static PortabilityClass ParsePortability(string s) => s.ToLowerInvariant() switch
    {
        "profile-relative" => PortabilityClass.ProfileRelative,
        "machine-locked" => PortabilityClass.MachineLocked,
        "partial" => PortabilityClass.Partial,
        _ => throw new RecipeValidationException($"unknown portabilityClass '{s}'"),
    };

    private static RestoreStrategy ParseStrategy(string s) => s.ToLowerInvariant() switch
    {
        "config-write" => RestoreStrategy.ConfigWrite,
        "merge-after-install" => RestoreStrategy.MergeAfterInstall,
        "replace" => RestoreStrategy.Replace,
        _ => throw new RecipeValidationException($"unknown restore.strategy '{s}'"),
    };

    private static RestorePhase ParsePhase(string s) => s.ToLowerInvariant() switch
    {
        "install" => RestorePhase.Install,
        "firstrunseed" or "first-run-seed" => RestorePhase.FirstRunSeed,
        "configwrite" or "config-write" => RestorePhase.ConfigWrite,
        _ => throw new RecipeValidationException($"unknown restore.phase '{s}'"),
    };

    // The wire values MIRROR the Kur module's InstallMethod string constants so the package install manifest and
    // the recipe speak the same vocabulary; an unknown value fails closed (a recipe never names a free command).
    private static RecipeInstallMethod ParseInstallMethod(string s) => s.ToLowerInvariant() switch
    {
        "install-winget" => RecipeInstallMethod.Winget,
        "install-npm" => RecipeInstallMethod.Npm,
        "install-url-manual" => RecipeInstallMethod.UrlManual,
        _ => throw new RecipeValidationException($"unknown install.method '{s}'"),
    };

    private static RestoreTier ParseRestoreTier(string s) => s.ToLowerInvariant() switch
    {
        "inventoryonly" or "inventory-only" => RestoreTier.InventoryOnly,
        "configcopy" or "config-copy" => RestoreTier.ConfigCopy,
        "mergeafterinstall" or "merge-after-install" => RestoreTier.MergeAfterInstall,
        _ => throw new RecipeValidationException($"unknown restoreTier '{s}'"),
    };

    private static CatalogTier ParseCatalogTier(string s) => s.ToLowerInvariant() switch
    {
        "trusted" => CatalogTier.Trusted,
        "community" => CatalogTier.Community,
        _ => throw new RecipeValidationException($"unknown catalogTier '{s}'"),
    };

    private static UpstreamDataLicense ParseUpstreamDataLicense(string s) => s.ToLowerInvariant() switch
    {
        "mit" => UpstreamDataLicense.Mit,
        "apache-2" => UpstreamDataLicense.Apache2,
        "bsd" => UpstreamDataLicense.Bsd,
        "gpl" => UpstreamDataLicense.Gpl,
        "cc-by" => UpstreamDataLicense.CcBy,
        "cc-by-nc-sa" => UpstreamDataLicense.CcByNcSa,
        "proprietary" => UpstreamDataLicense.Proprietary,
        "none" => UpstreamDataLicense.None,
        "unknown" => UpstreamDataLicense.Unknown,
        _ => throw new RecipeValidationException($"unknown upstreamDataLicense '{s}'"),
    };

    private static RecipeItemKind ParseItemKind(string s) => s.ToLowerInvariant() switch
    {
        "profilepath" or "profile-path" => RecipeItemKind.ProfilePath,
        "machineroot" or "machine-root" => RecipeItemKind.MachineRoot,
        "exportcmd" or "export-cmd" => RecipeItemKind.ExportCmd,
        "windowsetc" or "windows-etc" => RecipeItemKind.WindowsEtc,
        "manualtodo" or "manual-todo" => RecipeItemKind.ManualTodo,
        _ => throw new RecipeValidationException($"unknown items[].kind '{s}'"),
    };

    private static ExportKind ParseExportKind(string s) => s.ToLowerInvariant() switch
    {
        "wifiprofiles" or "wifi-profiles" => ExportKind.WifiProfiles,
        "registrysubtree" or "registry-subtree" => ExportKind.RegistrySubtree,
        "wingetlist" or "winget-list" => ExportKind.WingetList,
        "npmgloballist" or "npm-global-list" => ExportKind.NpmGlobalList,
        "pathdump" or "path-dump" => ExportKind.PathDump,
        "scheduledtasks" or "scheduled-tasks" => ExportKind.ScheduledTasks,
        _ => throw new RecipeValidationException($"unknown exportKind '{s}'"),
    };

    private static string ParseExpectedFormat(string s) => s.ToLowerInvariant() switch
    {
        "sqlite" => "sqlite",
        _ => throw new RecipeValidationException($"unknown items[].expectedFormat '{s}'"),
    };

    private static InstallerSource ParseInstallerSource(string s) => s.ToLowerInvariant() switch
    {
        "winget" => InstallerSource.Winget,
        "npm" => InstallerSource.Npm,
        "microsoftstore" or "microsoft-store" => InstallerSource.MicrosoftStore,
        "manualdownload" or "manual-download" => InstallerSource.ManualDownload,
        "existinginstaller" or "existing-installer" => InstallerSource.ExistingInstaller,
        "unknown" => InstallerSource.Unknown,
        _ => throw new RecipeValidationException($"unknown installerSource '{s}'"),
    };

    private static LicenseSource ParseLicenseSource(string s) => s.ToLowerInvariant() switch
    {
        "accountlogin" or "account-login" => LicenseSource.AccountLogin,
        "productkey" or "product-key" => LicenseSource.ProductKey,
        "licensefile" or "license-file" => LicenseSource.LicenseFile,
        "subscription" => LicenseSource.Subscription,
        "none" => LicenseSource.None,
        "unknown" => LicenseSource.Unknown,
        _ => throw new RecipeValidationException($"unknown licenseSource '{s}'"),
    };

    // ---- field helpers ---------------------------------------------------------------------

    private static void RejectUnknownFields(JsonElement obj, string where, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty p in obj.EnumerateObject())
            if (!set.Contains(p.Name))
                throw new RecipeValidationException($"unknown field '{p.Name}' in {where}");
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
            throw new RecipeValidationException($"missing or non-object field '{name}'");
        return el;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
            throw new RecipeValidationException($"missing or non-array field '{name}'");
        return el;
    }

    private static int RequireInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
            throw new RecipeValidationException($"missing or non-integer field '{name}'");
        return v;
    }

    private static bool RequireBool(JsonElement el, string name)
    {
        if (el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RecipeValidationException($"field '{name}' must be a boolean");
        return el.GetBoolean();
    }

    private static string RequireNonEmptyString(JsonElement parent, string name)
    {
        string? s = OptionalString(parent, name);
        if (string.IsNullOrWhiteSpace(s))
            throw new RecipeValidationException($"missing or empty field '{name}'");
        return s;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
            return null;
        if (el.ValueKind != JsonValueKind.String)
            throw new RecipeValidationException($"field '{name}' must be a string");
        return el.GetString();
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
            return Array.Empty<string>();
        if (el.ValueKind != JsonValueKind.Array)
            throw new RecipeValidationException($"field '{name}' must be an array");
        var list = new List<string>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new RecipeValidationException($"field '{name}' must contain only strings");
            string? s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return list;
    }

    private static IReadOnlyList<string> ParseGuidArray(JsonElement parent, string name)
    {
        IReadOnlyList<string> values = ParseStringArray(parent, name);
        foreach (string value in values)
            if (ProgramJoinKeys.TryProductCode(value) is null)
                throw new RecipeValidationException($"field '{name}' contains an invalid GUID '{value}'");
        return values.Select(v => v.ToLowerInvariant()).ToArray();
    }

    private static void ValidateRelativePath(string value, string field)
    {
        string normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
            || normalized.Contains('%', StringComparison.Ordinal)
            || normalized.Split('/', StringSplitOptions.None).Any(segment => segment == "..")
            || normalized.Any(char.IsControl))
        {
            throw new RecipeValidationException(
                $"field '{field}' must be a relative path with no root, environment token, parent traversal, or control character");
        }
    }

    private static bool MustForceInventoryOnly(
        PortabilityClass portability,
        RecipeDetect detect,
        IReadOnlyList<RecipeItem> items)
        => portability == PortabilityClass.MachineLocked
           || !IsProfileFolder(detect.KnownFolder)
           || items.Any(i => i.Kind is RecipeItemKind.MachineRoot or RecipeItemKind.WindowsEtc or RecipeItemKind.ExportCmd or RecipeItemKind.ManualTodo);

    private static bool IsProfileFolder(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;
}
