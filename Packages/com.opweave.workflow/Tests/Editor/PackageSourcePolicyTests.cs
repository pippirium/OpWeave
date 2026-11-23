#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] tests for the SPEC §0.6 SG-19 closed <c>allowedPackageSources</c>
    /// grammar and matcher (local:T-W2.1-07:allowed-package-sources,
    /// local:T-W2.1-08:scp-credential-detection): registry
    /// exact and terminal-<c>.*</c> entries, the three Git notations, the
    /// normalized host/repository exact match, every settings-time OW-CFG-002
    /// rejection class, OW-PERM-001 for unmatched candidate origins, OW-OP-006
    /// for credential-bearing ids, and the "no unsafe URL echo" guarantee of
    /// SPEC §G8.1.
    /// </summary>
    internal sealed class PackageSourcePolicyTests
    {
        private const string IdPath = "$.nodes[0].parameters.id";

        private static string SettingsJson(params string[] entries) =>
            "{\"version\":2,\"allowedPackageSources\":[" +
            string.Join(",", entries.Select(e => "\"" + e.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) +
            "]}";

        private static Result<OpWeaveSettings> LoadSettings(params string[] entries) =>
            OpWeaveSettingsLoader.Parse(SettingsJson(entries));

        private static PackageSourcePolicy LoadPolicy(params string[] entries)
        {
            var result = LoadSettings(entries);
            Assert.IsTrue(result.Ok,
                $"Fixture settings must load: {string.Join(", ", result.Diagnostics.Select(d => d.Code))}");
            return PackageSourcePolicy.For(result.Value!);
        }

        private static void AssertFieldFree(Diagnostic diagnostic, params string[] needles)
        {
            var fields = new[]
            {
                diagnostic.Message, diagnostic.Hint, diagnostic.JsonPath,
                diagnostic.NodeId, diagnostic.Port,
            };
            foreach (var field in fields)
            {
                if (field is null)
                {
                    continue;
                }
                foreach (var needle in needles)
                {
                    Assert.That(field.Contains(needle), Is.False,
                        $"Diagnostic field '{field}' must not echo '{needle}'.");
                }
            }
        }

        // ── Grammar: valid entries load and are preserved ────────────────────

        [TestCase("registry:com.unity.foo")]
        [TestCase("registry:a.b-c_d.1")]
        [TestCase("registry:x.*")]
        [TestCase("registry:com.unity.foo.*")]
        [TestCase("git:https://example.com/org/repo.git")]
        [TestCase("git:https://example.com:8443/org/repo.git")]
        [TestCase("git:https://10.0.0.1/org/repo")]
        [TestCase("git:https://host-name.example.com./r")]
        [TestCase("git:https://example.com/a%41b")]
        [TestCase("git:https://example.com/org/repo=release.git")]
        [TestCase("git:https://example.com/org/re,po.git")]
        [TestCase("git:https://example.com/o!$&'()*+,;=_-~/r")]
        [TestCase("git:https://example.com/org/repo:tag")]
        [TestCase("git:https://example.com/org/r@v1")]
        [TestCase("git:https://example.com:8443/o/r;e")]
        [TestCase("git:ssh://git@example.com/org/repo=release.git")]
        [TestCase("git:ssh://git@example.com/org/re,po")]
        [TestCase("git:ssh://git@example.com/org/repo:v1")]
        [TestCase("git:git@example.com:org/repo.git")]
        [TestCase("git:git@example.com:/abs/repo.git")]
        [TestCase("git:git@example.com:org/repo=release.git")]
        [TestCase("git:git@example.com:org/re,po")]
        [TestCase("git:git@example.com:org/repo:v1")]
        [TestCase("git:git@example.com:org/re@po.git")]
        [TestCase("git:git@example.com:o!$&'()*+,;=/r")]
        public void ValidEntriesLoadAndArePreserved(string entry)
        {
            var result = LoadSettings(entry);

            Assert.IsTrue(result.Ok,
                $"Entry '{entry}' must satisfy the grammar: " +
                string.Join(", ", result.Diagnostics.Select(d => d.Code)));
            Assert.That(result.Value!.AllowedPackageSources, Is.EqualTo(new[] { entry }));
        }

        // ── Grammar: out-of-grammar entries → OW-CFG-002 + element JsonPath ──

        private static readonly string[] s_invalidEntries =
        {
            // registry arm: shape and wildcard placement
            "",
            "registry:",
            "registry:Com.unity.*",
            "registry:COM.UNITY.FOO",
            "registry:com..x",
            "registry:.com.x",
            "registry:com.x.",
            "registry:com.*.x",
            "registry:*",
            "registry:com.**",
            "registry:com.un*ty",
            "registry:com.un ity",
            "registry:com.unity.*.x",
            // git arm: schemes and shapes outside the closed grammar
            "git:",
            "git:http://example.com/r",
            "git:file:///tmp/r.git",
            "git:git+https://example.com/r",
            "git:ftp://example.com/r",
            "git:repo/path.git",
            "git:/abs/path",
            "git:C:/abs/path",
            "https://example.com/r",
            // git arm: credentials and userinfo
            "git:https://user@example.com/r",
            "git:https://user:pw@example.com/r",
            "git:ssh://git:pw@example.com/r",
            // git arm: host rules (lowercase, no loopback/localhost/link-local,
            // no ambiguous numeric spellings)
            "git:https://Example.com/r",
            "git:https://EXAMPLE.com/r",
            "git:https://localhost/r",
            "git:https://x.localhost/r",
            "git:https://localhost./r",
            "git:https://127.0.0.1/r",
            "git:https://127.9.9.9/r",
            "git:https://169.254.1.2/r",
            "git:https://0.0.0.1/r",
            "git:https://2130706433/r",
            "git:https://127.1/r",
            "git:https://01.2.3.4/r",
            "git:https://0x7f.0.0.1/r",
            "git:https://[::1]/r",
            "git:https://[fe80::1]/r",
            // git arm: path rules (.., escapes, query, fragment, missing path)
            "git:https://example.com/../r",
            "git:https://example.com/a/../r",
            "git:https://example.com/r..x",
            "git:https://example.com/r%2e%2e",
            "git:https://example.com/r%2f",
            "git:https://example.com/r%20",
            "git:https://example.com/r%5c",
            "git:https://example.com/r%3d",
            "git:https://example.com/r%3a",
            "git:https://example.com/r%40",
            "git:https://example.com/r%zz",
            "git:https://example.com/r%4",
            "git:https://example.com/r%",
            "git:https://example.com/a[b",
            "git:https://example.com/a]b",
            "git:https://example.com/a|b",
            "git:https://example.com/a<b",
            "git:https://example.com/a\\b",
            "git:https://example.com/r?path=/x",
            "git:https://example.com/r#v1",
            "git:https://example.com",
            "git:https://example.com/",
            "git:https://example.com:0/r",
            "git:https://example.com:99999/r",
            "git:https://example.com:abc/r",
            "git:https://example.com:/r",
            "git:https://example.com:443:1/r",
            "git:https://example.com/a b/r",
            // ssh arm: the fixed git@ user and the same host/path rules
            "git:ssh://example.com/org/repo",
            "git:ssh://other@example.com/r",
            "git:ssh://git@example.com",
            "git:ssh://git@localhost/r",
            "git:ssh://git@Example.com/r",
            "git:ssh://git@example.com/../r",
            // scp arm
            "git:git@example.com",
            "git:git@example.com:",
            "git:git@localhost:org/repo",
            "git:git@Example.com:org/repo",
            "git:git@example.com:a b",
            "git:git@example.com:a/../b",
            "git:git@example.com:a?path=/x",
            "git:git@example.com:a#v1",
            // unknown prefixes
            "file:///r",
            "http://example.com/r",
            "com.unity.foo",
            "svn:https://example.com/r",
        };

        [Test]
        public void InvalidEntriesYieldOwCfg002AtElementJsonPath(
            [ValueSource(nameof(s_invalidEntries))] string entry)
        {
            var result = LoadSettings(entry);

            Assert.IsFalse(result.Ok, $"Entry '{entry}' must fail the grammar.");
            var matches = result.Diagnostics
                .Where(d => d.Code == ErrorCodes.OW_CFG_002).ToList();
            Assert.That(matches.Count, Is.EqualTo(1),
                $"Expected one OW-CFG-002, got: {string.Join(", ", result.Diagnostics.Select(d => d.Code))}");
            Assert.That(matches[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(matches[0].JsonPath, Is.EqualTo("$.allowedPackageSources[0]"));
        }

        [Test]
        public void InvalidEntryJsonPathFollowsElementIndex()
        {
            var result = LoadSettings(
                "registry:com.unity.*", "git:http://example.com/r");

            Assert.IsFalse(result.Ok);
            var matches = result.Diagnostics
                .Where(d => d.Code == ErrorCodes.OW_CFG_002).ToList();
            Assert.That(matches.Count, Is.EqualTo(1));
            Assert.That(matches[0].JsonPath, Is.EqualTo("$.allowedPackageSources[1]"));
        }

        [Test]
        public void InvalidEntryDiagnosticNeverEchoesTheEntry()
        {
            const string secretEntry = "git:https://user:s3cr3t@example.com/r";
            var result = LoadSettings(secretEntry);

            Assert.IsFalse(result.Ok);
            foreach (var diagnostic in result.Diagnostics)
            {
                AssertFieldFree(diagnostic, "s3cr3t", "user:", secretEntry);
            }
        }

        [Test]
        public void MixedValidAndInvalidEntriesReportEachBadElement()
        {
            var result = LoadSettings(
                "registry:com.unity.*", "git:http://a/r", "registry:Com.X", "git:https://ok.example/r");

            Assert.IsFalse(result.Ok);
            var errors = result.Diagnostics
                .Where(d => d.Code == ErrorCodes.OW_CFG_002).ToList();
            Assert.That(errors.Select(d => d.JsonPath),
                Is.EqualTo(new[] { "$.allowedPackageSources[1]", "$.allowedPackageSources[2]" }));
        }

        // ── Grammar: RFC 3986 pchar coverage across all Git notations ────────

        private static readonly char[] s_rfc3986PathChars =
            "-._~!$&'()*+,;=:@".ToCharArray();

        [Test]
        public void Rfc3986PathCharactersAreAcceptedInEveryGitNotation(
            [ValueSource(nameof(s_rfc3986PathChars))] char c)
        {
            foreach (var entry in new[]
            {
                $"git:https://example.com/o/c{c}d",
                $"git:ssh://git@example.com/o/c{c}d",
                $"git:git@example.com:o/c{c}d",
            })
            {
                var result = LoadSettings(entry);
                Assert.IsTrue(result.Ok,
                    $"Path character '{c}' must satisfy the SG-19 grammar in '{entry}': " +
                    string.Join(", ", result.Diagnostics.Select(d => d.Code)));
            }
        }

        private static readonly char[] s_nonPathChars =
            " \"<>[\\]^`{|}?#%".ToCharArray();

        [Test]
        public void NonPathCharactersStayRejectedInEveryGitNotation(
            [ValueSource(nameof(s_nonPathChars))] char c)
        {
            foreach (var entry in new[]
            {
                $"git:https://example.com/o/c{c}d",
                $"git:ssh://git@example.com/o/c{c}d",
                $"git:git@example.com:o/c{c}d",
            })
            {
                var result = LoadSettings(entry);
                Assert.IsFalse(result.Ok,
                    $"Character '{c}' is outside the repo-path grammar: '{entry}'");
            }
        }

        // ── Candidate parsing: registry and Git shapes ───────────────────────

        [Test]
        public void RegistryCandidateKeepsNameAndVersion()
        {
            Assert.IsTrue(PackageIdSource.TryParse("com.unity.foo@1.2.3", out var source));
            Assert.That(source!.Kind, Is.EqualTo(PackageIdKind.Registry));
            Assert.That(source.PackageName, Is.EqualTo("com.unity.foo"));
            Assert.That(source.Version, Is.EqualTo("1.2.3"));
        }

        [Test]
        public void GitCandidateSplitsRevisionAndSubPathFromTheOrigin()
        {
            Assert.IsTrue(PackageIdSource.TryParse(
                "https://example.com/org/repo.git?path=/sub/dir#v1.2.3", out var source));

            Assert.That(source!.Kind, Is.EqualTo(PackageIdKind.Git));
            // Origin matching input: revision and subPath are kept separately,
            // and the source URL itself is never rewritten (SPEC §0.6 SG-19).
            Assert.That(source.NormalizedOrigin, Is.EqualTo("https://example.com/org/repo.git"));
            Assert.That(source.SubPath, Is.EqualTo("/sub/dir"));
            Assert.That(source.Revision, Is.EqualTo("v1.2.3"));
        }

        // ── Matcher: registry rules ──────────────────────────────────────────

        [Test]
        public void RegistryPrefixMatchesOnlyNamesUnderTheDottedPrefix()
        {
            // SPEC §0.6: registry:com.unity.* matches com.unity.* names only,
            // never com.unityevil.
            var policy = LoadPolicy("registry:com.unity.*");

            Assert.IsNull(policy.CheckPackageId("com.unity.x", IdPath));
            Assert.IsNull(policy.CheckPackageId("com.unity.x@1.0.0", IdPath));
            Assert.IsNull(policy.CheckPackageId("com.unity.foo.bar", IdPath));

            foreach (var candidate in new[]
            {
                "com.unityevil", "com.unity", "com.unityevil.foo",
                "com.unity2d.foo", "Com.Unity.X",
            })
            {
                var diagnostic = policy.CheckPackageId(candidate, IdPath);
                Assert.That(diagnostic, Is.Not.Null, candidate);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        [Test]
        public void RegistryExactMatchesOnlyTheLiteralName()
        {
            var policy = LoadPolicy("registry:com.unity.foo");

            Assert.IsNull(policy.CheckPackageId("com.unity.foo", IdPath));
            Assert.IsNull(policy.CheckPackageId("com.unity.foo@2.3.4", IdPath));

            foreach (var candidate in new[] { "com.unity.foobar", "com.unity.foo.bar", "com.unity" })
            {
                Assert.That(policy.CheckPackageId(candidate, IdPath)!.Code,
                    Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        // ── Matcher: Git rules, normalized host + literal path ───────────────

        [Test]
        public void HttpsOriginMatchesExactlyAfterSchemeAndHostNormalization()
        {
            var policy = LoadPolicy("git:https://github.com/org/repo.git");

            // Same origin; revision and subPath ride along outside matching.
            Assert.IsNull(policy.CheckPackageId("https://github.com/org/repo.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://github.com/org/repo.git#v1.2.3", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://github.com/org/repo.git?path=/sub", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://github.com/org/repo.git?path=/sub#v1", IdPath));
            Assert.IsNull(policy.CheckPackageId("HTTPS://github.com/org/repo.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://GitHub.COM/org/repo.git", IdPath));

            // Any other notation is a different origin (SPEC §G8.1).
            foreach (var candidate in new[]
            {
                "https://github.com/Org/Repo.git",   // path case is significant
                "https://github.com/org/repo",       // missing .git
                "https://github.com/org/repo.git/",  // trailing slash
                "https://github.com/org/repo.git/extra",
                "https://github.com:443/org/repo.git", // explicit port ≠ none
                "https://github.com./org/repo.git",  // trailing-dot host
                "http://github.com/org/repo.git",
                "git+https://github.com/org/repo.git",
                "git://github.com/org/repo.git",
                "file:///tmp/repo.git",
                "ssh://git@github.com/org/repo.git",
                "git@github.com:org/repo.git",
                "https://github.com/org/../repo.git",
                "https://github.com/org/repo.git?path=/a/../b",
            })
            {
                var diagnostic = policy.CheckPackageId(candidate, IdPath);
                Assert.That(diagnostic, Is.Not.Null, candidate);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        [Test]
        public void ExplicitPortMatchesOnlyTheSamePort()
        {
            var policy = LoadPolicy("git:https://example.com:8443/o/r");

            Assert.IsNull(policy.CheckPackageId("https://example.com:8443/o/r", IdPath));
            Assert.That(policy.CheckPackageId("https://example.com/o/r", IdPath)!.Code,
                Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(policy.CheckPackageId("https://example.com:443/o/r", IdPath)!.Code,
                Is.EqualTo(ErrorCodes.OW_PERM_001));
        }

        [Test]
        public void SshOriginMatchesOnlyTheGitUserNotation()
        {
            var policy = LoadPolicy("git:ssh://git@example.com/org/repo");

            Assert.IsNull(policy.CheckPackageId("ssh://git@example.com/org/repo", IdPath));
            Assert.IsNull(policy.CheckPackageId("ssh://git@EXAMPLE.com/org/repo", IdPath));
            Assert.IsNull(policy.CheckPackageId("ssh://git@example.com/org/repo#main", IdPath));

            foreach (var candidate in new[]
            {
                "ssh://other@example.com/org/repo",
                "ssh://Git@example.com/org/repo",   // userinfo is not normalized
                "ssh://git@example.com:22/org/repo",
                "git@example.com:org/repo",        // SCP notation is a different origin
            })
            {
                Assert.That(policy.CheckPackageId(candidate, IdPath)!.Code,
                    Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        [Test]
        public void ScpOriginMatchesOnlyTheSameScpNotation()
        {
            var policy = LoadPolicy("git:git@example.com:org/repo.git");

            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@EXAMPLE.com:org/repo.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo.git#tag", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo.git?path=/s", IdPath));

            foreach (var candidate in new[]
            {
                "git@example.com:org/repo",
                "other@example.com:org/repo.git",
                "ssh://git@example.com/org/repo.git",
            })
            {
                Assert.That(policy.CheckPackageId(candidate, IdPath)!.Code,
                    Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        [Test]
        public void ScpOriginWithAtSignInRepoPathMatchesItsAllowlistEntry()
        {
            // SPEC §0.6 SG-19: '@' is an RFC 3986 pchar and valid in the SCP
            // repository path; the host/path boundary stays the first ':'
            // after the user.
            var policy = LoadPolicy("git:git@example.com:org/re@po.git");

            Assert.IsNull(policy.CheckPackageId("git@example.com:org/re@po.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@EXAMPLE.com:org/re@po.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/re@po.git#tag", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/re@po.git?path=/s", IdPath));

            foreach (var candidate in new[]
            {
                "git@example.com:org/re@po",
                "git@example.com:org/re@other.git",
                "other@example.com:org/re@po.git",
                "ssh://git@example.com/org/re@po.git",
                "https://example.com/org/re@po.git",
            })
            {
                var diagnostic = policy.CheckPackageId(candidate, IdPath);
                Assert.That(diagnostic, Is.Not.Null, candidate);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        [Test]
        public void ScpOriginWithSchemeSeparatorInRepoPathMatchesItsAllowlistEntry()
        {
            // SPEC §0.6 SG-19: ':' and '/' are repository-path characters, so a
            // '://' run inside an SCP path is path text, not a URL scheme — the
            // allowlisted entry and its identical candidate must agree.
            var policy = LoadPolicy("git:git@example.com:org/repo://name");

            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo://name", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@EXAMPLE.com:org/repo://name", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo://name#tag", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo://name?path=/s", IdPath));

            foreach (var candidate in new[]
            {
                "git@example.com:org/repo://other",
                "git@example.com:org/repo://",
                "other@example.com:org/repo://name",
                "ssh://git@example.com/org/repo://name",
                "https://example.com/org/repo://name",
            })
            {
                var diagnostic = policy.CheckPackageId(candidate, IdPath);
                Assert.That(diagnostic, Is.Not.Null, candidate);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        // ── SCP credential detection (local:T-W2.1-08) ─────────────────────
        // Credentials are judged only from the SCP userinfo: '://' and '@'
        // inside the repository path of a 'user@host:path' candidate are path
        // text (SPEC §0.6 SG-19), while a 'user:<secret>@' userinfo stays
        // credential-bearing even when the remainder contains '://' (§G8.1).

        [Test]
        public void ScpOriginWithSchemeSeparatorAndAtInRepoPathMatchesItsAllowlistEntry()
        {
            var policy = LoadPolicy("git:git@example.com:org/repo://na@me");

            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo://na@me", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@EXAMPLE.com:org/repo://na@me", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo://na@me#tag", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo://na@me?path=/s", IdPath));

            foreach (var candidate in new[]
            {
                "git@example.com:org/repo://na@other",
                "git@example.com:org/repo://name",
                "git@example.com:other/repo://na@me",
                "other@example.com:org/repo://na@me",
                "ssh://git@example.com/org/repo://na@me",
                "https://example.com/org/repo://na@me",
            })
            {
                var diagnostic = policy.CheckPackageId(candidate, IdPath);
                Assert.That(diagnostic, Is.Not.Null, candidate);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        [Test]
        public void ScpOriginWithAtThenSchemeSeparatorInRepoPathMatchesItsAllowlistEntry()
        {
            // '@' and then '://' inside the same repository path: the order of
            // the two path characters must not matter to the form decision.
            var policy = LoadPolicy("git:git@example.com:org/re@po://x");

            Assert.IsNull(policy.CheckPackageId("git@example.com:org/re@po://x", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/re@po://x#tag", IdPath));

            var diagnostic = policy.CheckPackageId("git@example.com:org/re@po://y", IdPath);
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
        }

        [Test]
        public void ScpCandidateWithPasswordUserinfoYieldsOwOp006EvenWithSchemeSeparatorInPath()
        {
            // The check runs before any allowlist decision: even a policy that
            // would allow the same host/path as a 'git@' source must not turn
            // the password-bearing candidate into OW-PERM-001 (or allow it).
            var policy = LoadPolicy("git:git@example.com:org/repo://x");

            var cases = new (string Id, string Secret)[]
            {
                ("git:passw0rd@example.com:org/repo://x", "passw0rd"),
                ("git:passw0rd@example.com:org/repo://na@me", "passw0rd"),
                ("git:passw0rd@example.com:org/repo", "passw0rd"),
                ("u:passw0rd@example.com:org/repo://x", "passw0rd"),
            };
            foreach (var (id, secret) in cases)
            {
                var diagnostic = policy.CheckPackageId(id, IdPath, nodeId: "add", port: "id");
                Assert.That(diagnostic, Is.Not.Null, id);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_OP_006), id);
                Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error), id);
                Assert.That(diagnostic.JsonPath, Is.EqualTo(IdPath), id);
                // No diagnostic field may echo the secret or the candidate.
                AssertFieldFree(diagnostic, secret, id);
            }
        }

        [Test]
        public void CredentialFreeScpCandidateWithSchemeSeparatorAndAtIsOwPerm001WhenUnmatched()
        {
            // Same path shape with the plain 'git@' user: not credential-bearing,
            // so an unmatched origin is OW-PERM-001, not OW-OP-006.
            var policy = LoadPolicy("git:git@example.com:org/repo.git");

            var diagnostic = policy.CheckPackageId("git@example.com:org/repo://na@me", IdPath);
            Assert.That(diagnostic, Is.Not.Null);
            Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error));
            AssertFieldFree(diagnostic, "repo://na@me", "na@me");
        }

        [Test]
        public void GitOriginsWithExtendedPathCharactersMatchExactly()
        {
            var policy = LoadPolicy(
                "git:https://example.com/org/repo=release.git",
                "git:ssh://git@example.com/org/re,po.git",
                "git:git@example.com:org/repo:v1");

            Assert.IsNull(policy.CheckPackageId("https://example.com/org/repo=release.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://example.com/org/repo=release.git#v1", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://example.com/org/repo=release.git?path=/s=1", IdPath));
            Assert.IsNull(policy.CheckPackageId("HTTPS://EXAMPLE.com/org/repo=release.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("ssh://git@example.com/org/re,po.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo:v1", IdPath));

            foreach (var candidate in new[]
            {
                // The percent-escaped spelling is a different origin text;
                // matching stays literal (SPEC §0.6 SG-19).
                "https://example.com/org/repo%3drelease.git",
                "https://example.com/org/repo=other.git",
                "https://example.com/org/repo=release",
                "ssh://git@example.com/org/repo:v1",
                "git@example.com:org/repo:v2",
                "git@example.com:org/repo",
            })
            {
                Assert.That(policy.CheckPackageId(candidate, IdPath)!.Code,
                    Is.EqualTo(ErrorCodes.OW_PERM_001), candidate);
            }
        }

        // ── Rule/candidate parity: one Git origin grammar for both parsers ──

        // One origin text feeds both parsers: the allowlist rule reads it
        // after the "git:" prefix, the candidate id parser reads it bare.
        // Entries cover every RFC 3986 path character class across all three
        // Git notations plus credential/authority-ambiguous forms. For each
        // entry both parse results must agree (SPEC §0.6 SG-19), and an
        // allowlisted source must match its identical candidate.
        private static readonly (string Origin, bool Valid)[] s_gitOriginParity =
        {
            // https:// notation: baseline and every path character class.
            ("https://example.com/org/repo.git", true),
            ("https://example.com/org/re@po.git", true),
            ("https://example.com/org/repo=rel.git", true),
            ("https://example.com/org/re,po.git", true),
            ("https://example.com/org/re+po.git", true),
            ("https://example.com/org/re~po.git", true),
            ("https://example.com/org/re%41po.git", true),
            ("https://example.com/org/repo:tag", true),
            ("https://example.com:8443/org/re@po.git", true),
            ("https://example.com/o!$&'()*+,;=_-~/r", true),
            // ssh://git@ notation.
            ("ssh://git@example.com/org/re@po.git", true),
            ("ssh://git@example.com/org/repo:v1", true),
            ("ssh://git@example.com/org/re%7epo.git", true),
            // SCP git@ notation, including '@' inside the repository path.
            ("git@example.com:org/repo.git", true),
            ("git@example.com:org/re@po.git", true),
            ("git@example.com:org/repo:v1", true),
            ("git@example.com:org/re,po+~.git", true),
            ("git@example.com:org/re%2dpo.git", true),
            ("git@example.com:/abs/re@po.git", true),
            ("git@example.com:o!$&'()*+,;=/r", true),
            // A '://' run inside an SCP repository path is path text, not a
            // scheme separator: the boundary is the first ':' that no '/'
            // precedes, identical on the rule and candidate sides.
            ("git@example.com:org/repo://name", true),
            ("git@example.com:org/repo://deep/x.git", true),
            ("git@example.com://org/repo", true),
            // The same '://' run combined with '@' path text stays path text.
            ("git@example.com:org/repo://na@me", true),
            ("git@example.com:org/re@po://x", true),
            ("git@example.com://org/re@po", true),
            // Credential and authority-ambiguous forms: rejected in both.
            ("https://user@example.com/r", false),
            ("https://user:pw@example.com/r", false),
            ("ssh://git:pw@example.com/r", false),
            ("ssh://other@example.com/r", false),
            ("ssh://example.com/r", false),
            ("git:p@example.com:org/repo", false),
            ("git:p@example.com:org/repo://x", false),
            ("u:p@example.com:org/repo://x", false),
            ("other@example.com:org/repo", false),
            ("git@@example.com:org/repo", false),
            ("git@localhost:org/repo", false),
            ("git@example.com:org/../repo", false),
            ("git@example.com:r%2e", false),
            ("git@example.com:", false),
            // SCP form rejects a missing git@ user, a '/' inside the authority
            // and an invalid repository path — '://' baggage included.
            ("example.com:org/repo", false),
            ("git@h/p:x", false),
            ("git@example.com:org/../repo://x", false),
            ("git@example.com:org/repo://x%20", false),
            ("https://example.com", false),
            ("https://example.com/", false),
            ("https://example.com:0/r", false),
            ("https://example.com:99999/r", false),
            ("https://example.com/r%2f", false),
            ("https://example.com/r%3a", false),
        };

        [Test]
        public void RuleAndCandidateGrammarsAgreeOnGitOrigins()
        {
            var policy = LoadPolicy(s_gitOriginParity
                .Where(e => e.Valid)
                .Select(e => "git:" + e.Origin)
                .ToArray());
            foreach (var (origin, valid) in s_gitOriginParity)
            {
                Assert.That(PackageSourceRule.TryParse("git:" + origin, out _),
                    Is.EqualTo(valid), $"rule 'git:{origin}'");
                Assert.That(PackageIdSource.TryParse(origin, out var source),
                    Is.EqualTo(valid), $"candidate '{origin}'");
                if (!valid)
                {
                    Assert.That(policy.CheckPackageId(origin, IdPath),
                        Is.Not.Null, origin);
                    continue;
                }
                Assert.That(source!.Kind, Is.EqualTo(PackageIdKind.Git), origin);
                Assert.That(source.NormalizedOrigin, Is.EqualTo(origin), origin);
                Assert.IsNull(policy.CheckPackageId(origin, IdPath), origin);
            }
        }

        [Test]
        public void EmptyAllowlistRejectsEverySource()
        {
            var policy = LoadPolicy();

            Assert.That(policy.CheckPackageId("com.unity.foo", IdPath)!.Code,
                Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(policy.CheckPackageId("https://github.com/o/r.git", IdPath)!.Code,
                Is.EqualTo(ErrorCodes.OW_PERM_001));
        }

        [Test]
        public void MultipleEntriesEachContributeTheirRule()
        {
            var policy = LoadPolicy(
                "registry:com.unity.*",
                "git:https://github.com/org/repo.git",
                "git:git@example.com:org/repo.git");

            Assert.IsNull(policy.CheckPackageId("com.unity.x", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://github.com/org/repo.git", IdPath));
            Assert.IsNull(policy.CheckPackageId("git@example.com:org/repo.git", IdPath));
            Assert.That(policy.CheckPackageId("https://other.example/o/r", IdPath)!.Code,
                Is.EqualTo(ErrorCodes.OW_PERM_001));
        }

        // ── Rejection classes: OW-OP-006 credentials, OW-PERM-001 the rest ───

        [Test]
        public void CredentialFormsYieldOwOp006WithoutEchoingSecrets()
        {
            var policy = LoadPolicy(
                "git:https://github.com/org/repo.git",
                "git:ssh://git@example.com/org/repo",
                "git:git@example.com:org/repo.git");

            var cases = new (string Id, string Secret)[]
            {
                ("https://user:passw0rd@github.com/org/repo.git", "passw0rd"),
                ("https://t0ken@github.com/org/repo.git", "t0ken"),
                ("https://user@github.com/org/repo.git", "user"),
                ("https://github.com/org/repo.git?access_token=s3cr3t", "s3cr3t"),
                ("https://github.com/org/repo.git?path=/s&token=s3cr3t", "s3cr3t"),
                ("ssh://git:passw0rd@example.com/org/repo", "passw0rd"),
                ("git:passw0rd@example.com:org/repo.git", "passw0rd"),
                ("http://u:p4ss@example.com/x", "p4ss"),
            };
            foreach (var (id, secret) in cases)
            {
                var diagnostic = policy.CheckPackageId(id, IdPath, nodeId: "add", port: "id");
                Assert.That(diagnostic, Is.Not.Null, id);
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_OP_006), id);
                Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error), id);
                Assert.That(diagnostic.JsonPath, Is.EqualTo(IdPath), id);
                Assert.That(diagnostic.NodeId, Is.EqualTo("add"), id);
                Assert.That(diagnostic.Port, Is.EqualTo("id"), id);
                AssertFieldFree(diagnostic, secret, id);
            }
        }

        [Test]
        public void UnmatchedAndUnrecognizableSourcesYieldOwPerm001WithoutEcho()
        {
            var policy = LoadPolicy("registry:com.unity.*", "git:https://github.com/org/repo.git");

            var candidates = new[]
            {
                "", "   ", "not a source", "com.foo@",
                "com.foo@1.0#rev", "com.foo@1.0?path=/x",
                "https://", "https://host", "https://host/a b",
                "https://host/x@y", "a@b@c", "https://[::1]/r",
                "https://github.com/other/repo.git",
                "org.elsewhere.pkg",
            };
            foreach (var candidate in candidates)
            {
                var diagnostic = policy.CheckPackageId(candidate, IdPath);
                Assert.That(diagnostic, Is.Not.Null, $"'{candidate}'");
                Assert.That(diagnostic!.Code, Is.EqualTo(ErrorCodes.OW_PERM_001), $"'{candidate}'");
                Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error), $"'{candidate}'");
                AssertFieldFree(diagnostic, "github.com");
            }
        }

        [Test]
        public void AllowedSourceProducesNoDiagnostic()
        {
            var policy = LoadPolicy("registry:com.unity.*");

            Assert.IsNull(policy.CheckPackageId("com.unity.foo", IdPath));
        }

        // ── Policy construction contract ─────────────────────────────────────

        [Test]
        public void CreateFailsClosedWithOwCfg002PerBadEntry()
        {
            var result = PackageSourcePolicy.Create(
                new[] { "registry:com.unity.*", "git:http://a/r" });

            Assert.IsFalse(result.Ok);
            var diagnostic = result.Diagnostics.Single();
            Assert.That(diagnostic.Code, Is.EqualTo(ErrorCodes.OW_CFG_002));
            Assert.That(diagnostic.JsonPath, Is.EqualTo("$.allowedPackageSources[1]"));
        }

        [Test]
        public void ForBuildsThePolicyFromLoadedSettings()
        {
            var settings = LoadSettings("registry:com.unity.*", "git:https://github.com/o/r").Value!;

            var policy = PackageSourcePolicy.For(settings);

            Assert.That(policy.Rules.Select(r => r.Kind), Is.EqualTo(new[]
            {
                PackageSourceKind.RegistryPrefix,
                PackageSourceKind.GitOrigin,
            }));
            Assert.IsNull(policy.CheckPackageId("com.unity.x", IdPath));
            Assert.IsNull(policy.CheckPackageId("https://github.com/o/r", IdPath));
        }
    }
}
