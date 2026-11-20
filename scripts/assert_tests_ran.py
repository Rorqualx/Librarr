#!/usr/bin/env python3
"""Fail unless every required test is in an NUnit result file and passed.

A test that does not run is not a failure as far as `dotnet test` is
concerned. Drop the env var that un-gates a suite, change a --filter,
rename a fixture or mark it [Ignore], and the step stays green while the
test it was there for quietly stops existing. This reads the result file
afterwards and checks the tests that matter are actually in it.

Usage: assert_tests_ran.py [--discovered-only|--passed-only] RESULTS_XML REQUIRED_LIST

REQUIRED_LIST has one entry per line; blank lines and `#` comments are
ignored. An entry is a test's full name, or a prefix of it -- a fixture or
a parameterised method -- and matches every test-case under it:

    NzbDrone.Api.Test.Frontend.IndexHtmlMapperFixture >=8
    NzbDrone.Playwright.Test.UrlBaseTest.ui_loads_with_every_request_under_the_url_base

Each entry must match at least one test-case (or at least N with `>=N`),
and every test-case it matches must have passed. Skipped, Ignored and
Inconclusive all count as not having run.

Those are two findings with different culprits, and a suite whose tests
reach a third party needs them judged separately:

    --discovered-only   only the count clause: the tests were collected and
                        run at all. Nobody else's server can change that, so
                        a failure here is our own wiring -- safe to block a
                        merge on.
    --passed-only       only the outcome clause. This is what reddens when
                        someone else's host is down, so a leg that talks to
                        one should report it without blocking.

With neither flag both clauses are checked, which is what a suite that
touches nothing external should do.
"""

import sys
import xml.etree.ElementTree as ET

# (discovered, passed) -> (what a green run proved, what a red one did not)
CLAUSES = {
    (True, True): ("discovered and passed", "did not run and pass"),
    (True, False): ("discovered", "were not discovered"),
    (False, True): ("passed", "did not pass"),
}


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
    # `entry + "<"` is for a GENERIC fixture. NUnit reports its cases as
    # Fixture<TypeArg>.method, but the entry has to name the fixture without
    # its type argument, because that is the form check_required_tests_wired.py
    # can resolve against `class Fixture<T>` in source. Without this an entry
    # for a generic fixture silently matches nothing, which is the one failure
    # mode this whole script exists to prevent.
    return (fullname == entry
            or fullname.startswith(entry + ".")
            or fullname.startswith(entry + "(")
            or fullname.startswith(entry + "<"))


def collected(results_xml, required):
    """How many test-cases the list matched in total.

    Reported on every green run, because --passed-only on its own cannot
    fail an entry that matched nothing -- that is --discovered-only's to
    report -- and "passed" over zero cases is the exact kind of hollow
    green this script exists to make visible.
    """
    names = [c.get("fullname", "") for c in ET.fromstring(results_xml).iter("test-case")]

    return sum(1 for name in names for entry, _ in required if matches(entry, name))


def check(results_xml, required, discovered=True, passed=True):
    root = ET.fromstring(results_xml)
    cases = [(c.get("fullname", ""), c.get("result", ""), c.get("label", "")) for c in root.iter("test-case")]
    problems = []

    for entry, minimum in required:
        found = [c for c in cases if matches(entry, c[0])]

        if discovered and len(found) < minimum:
            problems.append("{0}: expected at least {1} test case(s), found {2}".format(entry, minimum, len(found)))

        if not passed:
            continue

        for fullname, result, label in found:
            if result != "Passed":
                problems.append("{0}: {1}".format(fullname, label or result))

    return problems


def main(argv):
    args = argv[1:]
    discovered = passed = True

    while args and args[0].startswith("--"):
        flag = args.pop(0)

        if flag == "--discovered-only":
            passed = False
        elif flag == "--passed-only":
            discovered = False
        else:
            sys.stderr.write("unknown option: {0}\n\n".format(flag))
            sys.stderr.write(__doc__)
            return 2

    # Neither clause left is a no-op check that would report green having
    # verified nothing, which is the exact failure this script exists to
    # catch -- so it is an argument error, not an empty pass.
    if len(args) != 2 or not (discovered or passed):
        sys.stderr.write(__doc__)
        return 2

    results_path, required_path = args
    proved, failed = CLAUSES[(discovered, passed)]

    try:
        with open(results_path, encoding="utf-8") as f:
            results_xml = f.read()
    except OSError as e:
        print("::error::No test results at {0} ({1}), so nothing proves the required tests ran.".format(results_path, e.strerror))
        return 1

    with open(required_path, encoding="utf-8") as f:
        required = parse_required(f.read())

    problems = check(results_xml, required, discovered, passed)

    if problems:
        print("::error::Required tests from {0} {1}:".format(required_path, failed))

        for problem in problems:
            print("  " + problem)

        return 1

    print("{0}: {1} required entr{2} {3} ({4} test case(s)).".format(
        required_path, len(required), "y" if len(required) == 1 else "ies",
        proved, collected(results_xml, required)))

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
