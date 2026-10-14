#!/usr/bin/env python3
"""Tests for assert_tests_ran.py. Run: python3 scripts/test_assert_tests_ran.py

CI runs these before trusting the checker, because a checker that passes
everything looks exactly like a healthy suite.
"""

import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from assert_tests_ran import check, main, parse_required  # noqa: E402


def results(*cases):
    body = "".join(
        '<test-case fullname="{0}" result="{1}"{2} />'.format(
            name.replace("&", "&amp;").replace('"', "&quot;").replace("<", "&lt;"),
            result,
            ' label="{0}"'.format(label) if label else "")
        for name, result, label in cases)

    return '<test-run><test-suite type="Assembly">{0}</test-suite></test-run>'.format(body)


FIXTURE = "NzbDrone.Api.Test.Frontend.IndexHtmlMapperFixture"


class ParseRequiredTests(unittest.TestCase):
    def test_skips_comments_and_blank_lines(self):
        self.assertEqual(parse_required("# a comment\n\n  A.B  # trailing\n"), [("A.B", 1)])

    def test_reads_a_minimum(self):
        self.assertEqual(parse_required("A.B >=8"), [("A.B", 8)])

    def test_rejects_anything_else(self):
        with self.assertRaises(ValueError):
            parse_required("A.B C.D")


class CheckTests(unittest.TestCase):
    def test_passes_when_every_case_under_a_fixture_passed(self):
        xml = results((FIXTURE + ".a", "Passed", None), (FIXTURE + '.b("x")', "Passed", None))

        self.assertEqual(check(xml, [(FIXTURE, 2)]), [])

    def test_fails_when_the_test_is_missing(self):
        # A changed --filter, a renamed fixture, a deleted file.
        xml = results(("Other.Fixture.a", "Passed", None))

        self.assertEqual(len(check(xml, [(FIXTURE, 1)])), 1)

    def test_fails_when_the_test_was_ignored(self):
        # [Ignore], or a suite gate such as READARR_RUN_PLAYWRIGHT unset.
        xml = results((FIXTURE + ".a", "Skipped", "Ignored"))

        self.assertEqual(check(xml, [(FIXTURE, 1)]), [FIXTURE + ".a: Ignored"])

    def test_fails_when_inconclusive(self):
        xml = results((FIXTURE + ".a", "Inconclusive", None))

        self.assertEqual(len(check(xml, [(FIXTURE, 1)])), 1)

    def test_fails_when_failed(self):
        xml = results((FIXTURE + ".a", "Failed", None))

        self.assertEqual(len(check(xml, [(FIXTURE, 1)])), 1)

    def test_fails_below_the_minimum(self):
        # Most of a fixture's cases deleted still leaves it "present".
        xml = results((FIXTURE + ".a", "Passed", None))

        self.assertEqual(len(check(xml, [(FIXTURE, 8)])), 1)

    def test_matches_parameterised_cases_by_method_name(self):
        xml = results((FIXTURE + '.method("<a href=x>")', "Passed", None))

        self.assertEqual(check(xml, [(FIXTURE + ".method", 1)]), [])

    def test_a_prefix_does_not_match_a_longer_name(self):
        # "UrlBaseTest" must not be satisfied by "UrlBaseTestOld".
        xml = results(("Ns.UrlBaseTestOld.a", "Passed", None))

        self.assertEqual(len(check(xml, [("Ns.UrlBaseTest", 1)])), 1)


class MainTests(unittest.TestCase):
    def run_main(self, xml):
        with tempfile.TemporaryDirectory() as folder:
            required = os.path.join(folder, "required.txt")

            with open(required, "w") as f:
                f.write(FIXTURE + "\n")

            path = os.path.join(folder, "TestResult.xml")

            if xml is not None:
                with open(path, "w") as f:
                    f.write(xml)

            with open(os.devnull, "w") as devnull:
                stdout, sys.stdout = sys.stdout, devnull

                try:
                    return main(["assert_tests_ran.py", path, required])
                finally:
                    sys.stdout = stdout

    def test_exits_zero_when_satisfied(self):
        self.assertEqual(self.run_main(results((FIXTURE + ".a", "Passed", None))), 0)

    def test_exits_non_zero_when_not(self):
        self.assertEqual(self.run_main(results()), 1)

    def test_a_missing_results_file_fails_rather_than_passing(self):
        # No file means nothing ran -- e.g. the test step died early.
        self.assertEqual(self.run_main(None), 1)


if __name__ == "__main__":
    unittest.main()
