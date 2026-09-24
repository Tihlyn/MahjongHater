import unittest

from make_repo_json import make_entry, make_testing_entry


class TestingRepositoryTests(unittest.TestCase):
    def setUp(self):
        self.manifest = {"InternalName": "MahjongHater", "DalamudApiLevel": 15}
        self.stable = make_entry(self.manifest, "3.0.0", 100)
        self.packaged = dict(self.manifest, AssemblyVersion="3.0.0.1")

    def test_testing_changes_only_testing_fields_and_timestamp(self):
        result = make_testing_entry(self.stable, self.packaged, "3.0.0.1", 200, "Kan fixes")
        allowed = {"TestingAssemblyVersion", "TestingDalamudApiLevel", "TestingChangelog", "DownloadLinkTesting", "LastUpdate"}
        self.assertEqual({k: v for k, v in self.stable.items() if k not in allowed},
                         {k: v for k, v in result.items() if k not in allowed})
        self.assertEqual("3.0.0.0", result["AssemblyVersion"])
        self.assertEqual("3.0.0.1", result["TestingAssemblyVersion"])
        self.assertEqual("Kan fixes", result["TestingChangelog"])
        self.assertIn("/testing-3.0.0.1/latest.zip", result["DownloadLinkTesting"])
        self.assertNotIn("TestingAssemblyVersion", self.stable)

    def test_rejects_not_newer_than_stable(self):
        for version in ("2.9.9.9", "3.0.0.0"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                make_testing_entry(self.stable, dict(self.packaged, AssemblyVersion=version), version, 200, "")

    def test_rejects_testing_downgrade_but_allows_rerun(self):
        previous = make_testing_entry(self.stable, self.packaged, "3.0.0.1", 200, "")
        self.assertEqual(previous, make_testing_entry(previous, self.packaged, "3.0.0.1", 200, ""))
        previous["TestingAssemblyVersion"] = "3.0.0.2"
        with self.assertRaises(ValueError):
            make_testing_entry(previous, self.packaged, "3.0.0.1", 200, "")

    def test_rejects_bad_versions(self):
        for version in ("3.0.1", "3.0.0.1-beta", "3.0.0.65535", "3.0.0.01"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                make_testing_entry(self.stable, self.packaged, version, 200, "")

    def test_requires_matching_packaged_version_and_identity(self):
        for override in ({"AssemblyVersion": "3.0.0.0"}, {"InternalName": "AnotherPlugin"}, {"DalamudApiLevel": "15"}):
            with self.subTest(override=override), self.assertRaises(ValueError):
                make_testing_entry(self.stable, self.packaged | override, "3.0.0.1", 200, "")

    def test_requires_real_stable_release(self):
        for override in ({"IsTestingExclusive": True}, {"DownloadLinkInstall": ""}, {"DownloadLinkUpdate": ""}):
            with self.subTest(override=override), self.assertRaises(ValueError):
                make_testing_entry(self.stable | override, self.packaged, "3.0.0.1", 200, "")

    def test_next_stable_release_does_not_retain_outdated_testing(self):
        released = make_entry(self.manifest, "3.0.1", 300)
        self.assertEqual("3.0.1.0", released["AssemblyVersion"])
        self.assertNotIn("TestingAssemblyVersion", released)
        self.assertEqual(released["DownloadLinkInstall"], released["DownloadLinkTesting"])


if __name__ == "__main__":
    unittest.main()
