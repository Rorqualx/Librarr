#!/usr/bin/env python3
"""Tests for assert_tests_ran.py. Run: python3 scripts/test_assert_tests_ran.py

CI runs these before trusting the checker, because a checker that passes
everything looks exactly like a healthy suite.
"""

import io
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from assert_tests_ran import check, collected, main, parse_required  # noqa: E402


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

    def test_matches_a_generic_fixture_without_its_type_argument(self):
        # NUnit reports HttpClientFixture<ManagedHttpDispatcher>.x, but the
        # entry names the fixture as source declares it, with no type argument.
        xml = results(("Ns.HttpClientFixture<ManagedHttpDispatcher>.a", "Passed", None))

        self.assertEqual(check(xml, [("Ns.HttpClientFixture", 1)]), [])

    def test_a_generic_prefix_does_not_match_a_longer_name(self):
        xml = results(("Ns.HttpClientFixtureOld<D>.a", "Passed", None))

        self.assertEqual(len(check(xml, [("Ns.HttpClientFixture", 1)])), 1)

    def test_a_skipped_generic_case_fails(self):
        # The httpbin host being unreachable self-skips the whole fixture.
        xml = results(("Ns.HttpClientFixture<ManagedHttpDispatcher>.a", "Skipped", "Inconclusive"))

        self.assertEqual(len(check(xml, [("Ns.HttpClientFixture", 1)])), 1)

    def test_a_prefix_does_not_match_a_longer_name(self):
        # "UrlBaseTest" must not be satisfied by "UrlBaseTestOld".
        xml = results(("Ns.UrlBaseTestOld.a", "Passed", None))

        self.assertEqual(len(check(xml, [("Ns.UrlBaseTest", 1)])), 1)


class CollectedTests(unittest.TestCase):
    def test_counts_every_matched_case(self):
        xml = results((FIXTURE + ".a", "Passed", None),
                      (FIXTURE + ".b", "Inconclusive", "Inconclusive"),
                      ("Other.Fixture.c", "Passed", None))

        self.assertEqual(collected(xml, [(FIXTURE, 1)]), 2)

    def test_counts_nothing_when_nothing_matched(self):
        self.assertEqual(collected(results(), [(FIXTURE, 54)]), 0)


class ClauseTests(unittest.TestCase):
    """--discovered-only and --passed-only split `check` by whose fault it is.

    The case that matters is the network leg: when its third-party host is
    unreachable the fixture's [OneTimeSetUp] calls Assert.Inconclusive, and
    NUnit still reports every case -- discovered, not passed. That has to
    leave the blocking half green and the non-blocking half red.
    """

    SKIPPED = results((FIXTURE + ".a", "Inconclusive", "Inconclusive"))

    def test_discovered_only_passes_a_case_that_ran_but_did_not_pass(self):
        self.assertEqual(check(self.SKIPPED, [(FIXTURE, 1)], passed=False), [])

    def test_discovered_only_still_fails_when_nothing_was_collected(self):
        problems = check(results(), [(FIXTURE, 1)], passed=False)

        self.assertEqual(len(problems), 1)
        self.assertIn("found 0", problems[0])

    def test_passed_only_fails_a_case_that_ran_but_did_not_pass(self):
        self.assertEqual(len(check(self.SKIPPED, [(FIXTURE, 1)], discovered=False)), 1)

    def test_passed_only_ignores_a_fixture_that_was_never_collected(self):
        # Not this clause's blame to report -- --discovered-only owns it,
        # and that is the half that blocks.
        self.assertEqual(check(results(), [(FIXTURE, 54)], discovered=False), [])

    def test_the_default_checks_both(self):
        self.assertEqual(len(check(results(), [(FIXTURE, 1)])), 1)
        self.assertEqual(len(check(self.SKIPPED, [(FIXTURE, 1)])), 1)


class MainTests(unittest.TestCase):
    def run_main(self, xml, *flags):
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
                stderr, sys.stderr = sys.stderr, devnull

                try:
                    return main(["assert_tests_ran.py"] + list(flags) + [path, required])
                finally:
                    sys.stdout, sys.stderr = stdout, stderr

    def output(self, xml, *flags):
        """run_main, but returning what it printed."""
        captured = io.StringIO()
        stdout, sys.stdout = sys.stdout, captured

        try:
            self.run_main_into(xml, flags)
        finally:
            sys.stdout = stdout

        return captured.getvalue()

    def run_main_into(self, xml, flags):
        with tempfile.TemporaryDirectory() as folder:
            required = os.path.join(folder, "required.txt")

            with open(required, "w") as f:
                f.write(FIXTURE + "\n")

            path = os.path.join(folder, "TestResult.xml")

            with open(path, "w") as f:
                f.write(xml)

            return main(["assert_tests_ran.py"] + list(flags) + [path, required])

    def test_exits_zero_when_satisfied(self):
        self.assertEqual(self.run_main(results((FIXTURE + ".a", "Passed", None))), 0)

    def test_exits_non_zero_when_not(self):
        self.assertEqual(self.run_main(results()), 1)

    def test_a_missing_results_file_fails_rather_than_passing(self):
        # No file means nothing ran -- e.g. the test step died early.
        self.assertEqual(self.run_main(None), 1)

    def test_a_missing_results_file_fails_under_either_clause(self):
        # Neither clause can be judged from a file that is not there, so
        # both report it rather than the blocking one quietly passing.
        self.assertEqual(self.run_main(None, "--discovered-only"), 1)
        self.assertEqual(self.run_main(None, "--passed-only"), 1)

    def test_a_green_run_says_how_many_cases_it_judged(self):
        # --passed-only cannot fail an entry that matched nothing, so the
        # count is the only thing on a green run that distinguishes "the 54
        # passed" from "nothing ran and nothing therefore failed".
        self.assertIn("(0 test case(s))", self.output(results(), "--passed-only"))
        self.assertIn(
            "(2 test case(s))",
            self.output(results((FIXTURE + ".a", "Passed", None),
                                (FIXTURE + ".b", "Passed", None))))

    def test_an_unknown_option_is_an_argument_error(self):
        self.assertEqual(self.run_main(results(), "--discovered"), 2)

    def test_asking_for_neither_clause_is_an_argument_error(self):
        # Would otherwise check nothing and report green doing it.
        self.assertEqual(
            self.run_main(results((FIXTURE + ".a", "Passed", None)),
                          "--discovered-only", "--passed-only"),
            2)


if __name__ == "__main__":
    unittest.main()
