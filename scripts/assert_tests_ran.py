#!/usr/bin/env python3
"""Fail unless every required test is in an NUnit result file and passed.

A test that does not run is not a failure as far as `dotnet test` is
concerned. Drop the env var that un-gates a suite, change a --filter,
rename a fixture or mark it [Ignore], and the step stays green while the
test it was there for quietly stops existing. This reads the result file
afterwards and checks the tests that matter are actually in it.

Usage: assert_tests_ran.py RESULTS_XML REQUIRED_LIST

REQUIRED_LIST has one entry per line; blank lines and `#` comments are
ignored. An entry is a test's full name, or a prefix of it -- a fixture or
a parameterised method -- and matches every test-case under it:

    NzbDrone.Api.Test.Frontend.IndexHtmlMapperFixture >=8
    NzbDrone.Playwright.Test.UrlBaseTest.ui_loads_with_every_request_under_the_url_base

Each entry must match at least one test-case (or at least N with `>=N`),
and every test-case it matches must have passed. Skipped, Ignored and
Inconclusive all count as not having run.
"""

import sys
import xml.etree.ElementTree as ET


def parse_required(text):
    required = []

    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.split("#", 1)[0].strip()

        if not line:
            continue

        parts = line.split()
        minimum = 1

        if len(parts) == 2 and parts[1].startswith(">="):
            minimum = int(parts[1][2:])
        elif len(parts) != 1:
            raise ValueError("line {0}: expected 'NAME' or 'NAME >=N', got {1!r}".format(number, raw))

        required.append((parts[0], minimum))

    return required


def matches(entry, fullname):
    return fullname == entry or fullname.startswith(entry + ".") or fullname.startswith(entry + "(")


def check(results_xml, required):
    root = ET.fromstring(results_xml)
    cases = [(c.get("fullname", ""), c.get("result", ""), c.get("label", "")) for c in root.iter("test-case")]
    problems = []

    for entry, minimum in required:
        found = [c for c in cases if matches(entry, c[0])]

        if len(found) < minimum:
            problems.append("{0}: expected at least {1} test case(s), found {2}".format(entry, minimum, len(found)))

        for fullname, result, label in found:
            if result != "Passed":
                problems.append("{0}: {1}".format(fullname, label or result))

    return problems


def main(argv):
    if len(argv) != 3:
        sys.stderr.write(__doc__)
        return 2

    try:
        with open(argv[1], encoding="utf-8") as f:
            results_xml = f.read()
    except OSError as e:
        print("::error::No test results at {0} ({1}), so nothing proves the required tests ran.".format(argv[1], e.strerror))
        return 1

    with open(argv[2], encoding="utf-8") as f:
        required = parse_required(f.read())

    problems = check(results_xml, required)

    if problems:
        print("::error::Required tests from {0} did not run and pass:".format(argv[2]))

        for problem in problems:
            print("  " + problem)

        return 1

    print("All {0} required test entr{1} from {2} ran and passed.".format(
        len(required), "y" if len(required) == 1 else "ies", argv[2]))

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
