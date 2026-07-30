using System.Text;
using System.Text.RegularExpressions;
using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public sealed class ContentSignatureClassifierTests
{
    /// <summary>
    /// The single timeout for every edge of every handshake in this class — both event waits and both joins.
    /// One owner on purpose: a timeout is what releases a thread that was supposed to stay blocked, so if the
    /// edges could drift apart, which false-ordering windows exist would change silently.
    /// </summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private static readonly byte[] SyntheticDpapiHeader =
    [
        0x01, 0x00, 0x00, 0x00,
        0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
        0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB,
    ];

    [Fact]
    public void Synthetic_dpapi_provider_header_is_machine_bound()
    {
        byte[] bytes = [0x7B, 0x00, .. SyntheticDpapiHeader, 0x00, 0x7D];

        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasDpapiBlob);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Theory]
    [InlineData("owner=S-1-5-21-111111111-222222222-333333333-1001")]
    [InlineData("machineGuid={01234567-89ab-cdef-0123-456789abcdef}")]
    public void Text_bindings_are_recognized_from_synthetic_content(string text)
    {
        ContentSignature signature = ContentSignatureClassifier.Classify(Encoding.UTF8.GetBytes(text));

        Assert.True(signature.HasSidBinding || signature.HasMachineGuidBinding);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Three_absolute_user_profile_literals_trigger_path_binding()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            """{"a":"C:\Users\alice\one","b":"C:\Users\alice\two","c":"D:\Users\bob\three"}""");

        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasAbsolutePathBinding);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Theory]
    [InlineData(@"C:\Users\Alice Smith\AppData\Roaming\Tool\settings.json")]
    [InlineData(@"C:\\Users\\Alice Smith\\AppData\\Roaming\\Tool\\settings.json")]
    [InlineData("C:/Users/Alice Smith/AppData/Roaming/Tool/settings.json")]
    [InlineData(@"D:\Profiles\Alice Smith\AppData\Roaming\Tool\settings.json")]
    public void This_machine_profile_roots_detect_backslash_escaped_forward_and_spaced_forms(string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{{\"recent\":\"{path}\"}}");

        ContentSignature signature = ContentSignatureClassifier.Classify(
            bytes.AsSpan(),
            new ContentSignatureOptions([@"C:\Users\Alice Smith", @"D:\Profiles"]));

        Assert.True(signature.HasAbsolutePathBinding);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Utf16_profile_root_literal_is_detected()
    {
        byte[] bytes = Encoding.Unicode.GetBytes(@"path=D:\Profiles\Alice Smith\AppData\Roaming\Tool");

        ContentSignature signature = ContentSignatureClassifier.Classify(
            bytes.AsSpan(),
            new ContentSignatureOptions([@"D:\Profiles"]));

        Assert.True(signature.HasAbsolutePathBinding);
    }

    [Fact]
    public void Unexpected_sqlite_header_blocks_claim_without_machine_bound_label()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("SQLite format 3\0synthetic");

        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasSqliteHeader);
        Assert.True(signature.HasUnexpectedSqliteHeader);
        Assert.False(signature.HasCredentialStoreHeader);
        Assert.False(signature.HasMachineBoundContent);
        Assert.True(signature.BlocksPortabilityClaim);
    }

    [Fact]
    public void Expected_sqlite_header_does_not_block_claim()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("SQLite format 3\0synthetic");

        ContentSignature signature = ContentSignatureClassifier.Classify(
            bytes.AsSpan(),
            new ContentSignatureOptions(Array.Empty<string>(), ExpectedFormat: "sqlite"));

        Assert.True(signature.HasSqliteHeader);
        Assert.False(signature.HasUnexpectedSqliteHeader);
        Assert.False(signature.HasMachineBoundContent);
        Assert.False(signature.BlocksPortabilityClaim);
    }

    [Theory]
    [MemberData(nameof(CredentialStoreHeaders))]
    public void Credential_store_headers_are_conservatively_machine_bound(byte[] bytes)
    {
        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasCredentialStoreHeader);
        Assert.True(signature.HasMachineBoundContent);
    }

    public static TheoryData<byte[]> CredentialStoreHeaders => new()
    {
        new byte[] { 0x01, 0x02, 0x57, 0xFB, 0x80, 0x8B, 0x24, 0x75, 0x47, 0xDB },
        Encoding.ASCII.GetBytes("MANIFEST-000007\n"),
    };

    [Fact]
    public void Benign_content_does_not_fabricate_a_machine_binding()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("""{"theme":"dark","fontSize":14}""");
        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.False(signature.HasMachineBoundContent);
        Assert.Equal(bytes.Length, signature.BytesInspected);
    }

    [Fact]
    public void Stream_classifier_reads_no_more_than_the_requested_cap()
    {
        byte[] bytes = new byte[256];
        SyntheticDpapiHeader.CopyTo(bytes, 128);
        using var stream = new MemoryStream(bytes);

        ContentSignature signature = ContentSignatureClassifier.Classify(stream, maxBytes: 64);

        Assert.Equal(64, signature.BytesInspected);
        Assert.False(signature.HasDpapiBlob);
    }

    [Fact]
    public void Inconclusive_is_fail_closed()
    {
        ContentSignature signature = ContentSignature.Inconclusive();

        Assert.True(signature.IsInconclusive);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Directory_cloud_placeholders_block_claim_without_machine_bound_label()
    {
        ContentSignature signature = ContentSignatureClassifier.MergeDirectory(
            Array.Empty<(string RelativePath, ContentSignature Signature)>(),
            filesTotalSeen: 0,
            cloudPlaceholdersSkipped: 2);

        Assert.True(signature.IsDirectorySignature);
        Assert.Equal(0, signature.DirectoryFilesSampled);
        Assert.Equal(0, signature.DirectoryFilesTotalSeen);
        Assert.Equal(2, signature.DirectoryCloudPlaceholdersSkipped);
        Assert.False(signature.HasMachineBoundContent);
        Assert.True(signature.BlocksPortabilityClaim);
    }

    [Fact]
    public void Directory_subtree_skips_block_claim_without_poisoning_reachable_samples()
    {
        ContentSignature clean = ContentSignatureClassifier.Classify(Encoding.UTF8.GetBytes("theme=dark"));
        ContentSignature signature = ContentSignatureClassifier.MergeDirectory(
            [("reachable/settings.json", clean)],
            filesTotalSeen: 1,
            subtreesSkipped: 1);

        Assert.True(signature.IsDirectorySignature);
        Assert.Equal(1, signature.DirectoryFilesSampled);
        Assert.Equal(1, signature.DirectoryFilesTotalSeen);
        Assert.Equal(1, signature.DirectorySubtreesSkipped);
        Assert.Equal(ContentProbeStatus.Complete, signature.Status);
        Assert.False(signature.HasMachineBoundContent);
        Assert.True(signature.BlocksPortabilityClaim);
    }

    [Fact]
    public void Profile_root_regex_timeout_is_not_machine_bound()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(@"path=C:\Users\Alice\AppData\Roaming\Tool");
        ContentSignatureClassifier.ForceProfileRootRegexTimeoutForTests = true;
        try
        {
            ContentSignature signature = ContentSignatureClassifier.Classify(
                bytes.AsSpan(),
                new ContentSignatureOptions([@"C:\Users\Alice"]));

            Assert.Equal(ContentProbeStatus.ProbeTimedOut, signature.Status);
            Assert.False(signature.HasMachineBoundContent);
            Assert.True(signature.BlocksPortabilityClaim);
        }
        finally
        {
            ContentSignatureClassifier.ForceProfileRootRegexTimeoutForTests = false;
        }
    }

    /// <summary>
    /// The timeout override must not be visible to a classification running on another thread. This is the
    /// guard for a real intermittent failure: as a process-global static, the flag set by
    /// <see cref="Profile_root_regex_timeout_is_not_machine_bound"/> leaked into a parallel xUnit collection
    /// and made <c>Win32ContentSignatureProbeTests</c> see <see cref="ContentProbeStatus.ProbeTimedOut"/> on
    /// a clean file, blocking its portability claim.
    /// <para>
    /// A race cannot be proven by re-running the racing test, so this asserts the invariant directly with a
    /// handshake that holds the window open — deterministic, no timing luck. Removing <c>[ThreadStatic]</c>
    /// from the backing field turns this red with Complete vs ProbeTimedOut.
    /// </para>
    /// <para>
    /// Both timed waits are asserted rather than discarded. A discarded wait result is what turns a handshake
    /// back into timing luck: on timeout the observer would be released before the setter established the
    /// window, and the guard would pass vacuously under the very mutation it exists to reject.
    /// </para>
    /// </summary>
    [Fact]
    public void Profile_root_regex_timeout_override_is_confined_to_the_setting_thread()
    {
        using var flagIsSet = new ManualResetEventSlim(false);
        using var otherThreadDone = new ManualResetEventSlim(false);
        ContentProbeStatus observedOnOtherThread = ContentProbeStatus.Complete;
        Exception? otherThreadFailure = null;
        bool setterSawObserverFinish = false;
        bool observerSawFlagSet = false;

        var setter = new Thread(() =>
        {
            ContentSignatureClassifier.ForceProfileRootRegexTimeoutForTests = true;
            try
            {
                flagIsSet.Set();
                setterSawObserverFinish = otherThreadDone.Wait(HandshakeTimeout);
            }
            finally
            {
                ContentSignatureClassifier.ForceProfileRootRegexTimeoutForTests = false;
            }
        });

        var observer = new Thread(() =>
        {
            try
            {
                observerSawFlagSet = flagIsSet.Wait(HandshakeTimeout);
                ContentSignature signature = ContentSignatureClassifier.Classify(
                    Encoding.UTF8.GetBytes("theme=dark").AsSpan(),
                    new ContentSignatureOptions([@"C:\Users\Alice"]));
                observedOnOtherThread = signature.Status;
            }
            catch (Exception ex)
            {
                otherThreadFailure = ex;
            }
            finally
            {
                otherThreadDone.Set();
            }
        });

        setter.Start();
        observer.Start();
        Assert.True(observer.Join(HandshakeTimeout), "observer thread did not finish");
        Assert.True(setter.Join(HandshakeTimeout), "setter thread did not finish");

        Assert.Null(otherThreadFailure);
        Assert.True(observerSawFlagSet, "observer classified without waiting for the flag — ordering not established");
        Assert.True(setterSawObserverFinish, "setter released the window before the observer finished");
        Assert.Equal(ContentProbeStatus.Complete, observedOnOtherThread);
    }

    /// <summary>
    /// The cache entry published for a profile-root key is reused by a later classification, and stays reused
    /// while another thread classifies against a different root.
    /// <para>
    /// The claim is deliberately about the <i>published entry</i>, not about compiling exactly once.
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,System.Func{TKey,TValue})"/>
    /// documents that its value factory may run more than once for one key under concurrency while only one
    /// value is published; the extra build is discarded and every caller still receives the published instance.
    /// A stronger exactly-once guarantee would need a <c>Lazy&lt;Regex&gt;</c> per key, which buys nothing here:
    /// a duplicate build can only happen on a first-touch same-key race, costs one wasted compile, and cannot
    /// produce two live instances. Reference identity is therefore the honest assertion, and the wording matches it.
    /// </para>
    /// <para>
    /// Identity is asserted for this test's own key rather than by the size of the shared cache. The size cannot
    /// carry the claim: the suite parallelises at xUnit collection level, so a concurrent <c>Classify</c> with a
    /// different profile root inserts entries between the two reads. That is measured, not hypothesised — see
    /// the commit that introduced this test. A per-key lookup is unaffected by any other key, so the
    /// interference below is proof rather than a defect: the assert holds <i>because</i> the second
    /// classification runs after a foreign insertion, on every run, with no timing luck.
    /// </para>
    /// <para>
    /// Every timed wait is asserted, so a timeout fails the test instead of silently releasing a thread that was
    /// supposed to stay blocked, and the ordering is observed <i>on the owner thread</i> — reading it after the
    /// joins would only prove the foreign key exists eventually. There is no cache reset: clearing a
    /// process-wide cache is itself hostile to the collections running alongside this one.
    /// </para>
    /// </summary>
    [Fact]
    public void Profile_root_regexes_are_cached_across_classifications()
    {
        const string OwnRoot = @"C:\Users\CacheIdentityProbe";
        const string ForeignRoot = @"D:\Profiles\CacheIdentityInterferer";

        // Every classification also adds the ambient user profile and C:\Users as candidates, so key uniqueness
        // is not established by "no other test uses this literal" alone. On a host whose profile root equalled
        // one of these, an unrelated classification could populate the key and the ordering check below would
        // stop meaning anything. Refuse rather than silently degrade.
        string ambientProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(
            OwnRoot.Equals(ambientProfile, StringComparison.OrdinalIgnoreCase)
            || ForeignRoot.Equals(ambientProfile, StringComparison.OrdinalIgnoreCase),
            $"a chosen root collides with this host's ambient profile root ({ambientProfile})");

        using var firstClassificationDone = new ManualResetEventSlim(false);
        using var interferenceDone = new ManualResetEventSlim(false);
        byte[] bytes = Encoding.UTF8.GetBytes("""{"theme":"dark"}""");
        Regex? firstInstance = null;
        Regex? secondInstance = null;
        Regex? foreignInstanceSeenBeforeSecondClassification = null;
        Exception? ownerFailure = null;
        Exception? interfererFailure = null;
        bool ownerSawInterference = false;
        bool interfererSawFirstClassification = false;

        var owner = new Thread(() =>
        {
            try
            {
                _ = ContentSignatureClassifier.Classify(bytes.AsSpan(), new ContentSignatureOptions([OwnRoot]));
                firstInstance = ContentSignatureClassifier.PeekProfileRootRegexForTests(OwnRoot, jsonEscapedBackslashes: false);
                firstClassificationDone.Set();

                ownerSawInterference = interferenceDone.Wait(HandshakeTimeout);

                // Observed here, before the second classification, not after the joins: the claim is that the
                // foreign insertion had already happened, and only this thread can witness that ordering.
                foreignInstanceSeenBeforeSecondClassification =
                    ContentSignatureClassifier.PeekProfileRootRegexForTests(ForeignRoot, jsonEscapedBackslashes: false);

                _ = ContentSignatureClassifier.Classify(bytes.AsSpan(), new ContentSignatureOptions([OwnRoot]));
                secondInstance = ContentSignatureClassifier.PeekProfileRootRegexForTests(OwnRoot, jsonEscapedBackslashes: false);
            }
            catch (Exception ex)
            {
                ownerFailure = ex;
            }
        });

        var interferer = new Thread(() =>
        {
            try
            {
                interfererSawFirstClassification = firstClassificationDone.Wait(HandshakeTimeout);
                _ = ContentSignatureClassifier.Classify(bytes.AsSpan(), new ContentSignatureOptions([ForeignRoot]));
            }
            catch (Exception ex)
            {
                interfererFailure = ex;
            }
            finally
            {
                interferenceDone.Set();
            }
        });

        owner.Start();
        interferer.Start();
        Assert.True(owner.Join(HandshakeTimeout), "owner thread did not finish");
        Assert.True(interferer.Join(HandshakeTimeout), "interferer thread did not finish");

        Assert.Null(ownerFailure);
        Assert.Null(interfererFailure);

        // The arrangement must not be vacuous. Without these three, the test would still pass if the interferer
        // never ran, and would then assert caching with no concurrency at all.
        Assert.True(interfererSawFirstClassification, "interferer classified before the owner's first classification");
        Assert.True(ownerSawInterference, "owner reclassified before the interference completed");
        Assert.NotNull(foreignInstanceSeenBeforeSecondClassification);

        Assert.NotNull(firstInstance);
        Assert.Same(firstInstance, secondInstance);
    }
}
