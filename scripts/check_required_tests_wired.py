#!/usr/bin/env python3
"""Pre-commit check that the required regression tests are still wired up.

CI proves the tests in .github/required-tests/*.txt ran and passed, by
reading the result files (assert_tests_ran.py). That only says so after a
push. This catches the same breakages before the commit, statically, in
well under a second:

  - a required test's fixture or method no longer exists (renamed, deleted)
  - it, or its fixture, is marked [Ignore] or [Explicit]
  - it carries a category its CI step filters out
  - it lost the category its CI step selects it BY
  - the CI step no longer un-gates the suite or no longer runs the check
  - the blocking half of a check split by blame stopped blocking
  - a list exists that no CI step reads

Exit 0 when all is well, 1 with one line per problem otherwise.
Run from anywhere: python3 scripts/check_required_tests_wired.py
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LIST_DIR = os.path.join(ROOT, ".github", "required-tests")
WORKFLOW = os.path.join(ROOT, ".github", "workflows", "build.yml")
TEST_SH = os.path.join(ROOT, "test.sh")

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from assert_tests_ran import parse_required  # noqa: E402

TEST_ATTRIBUTE = re.compile(r"\[\s*(Test|TestCase|TestCaseSource)\b")
DISABLED_ATTRIBUTE = re.compile(r"\[\s*(Ignore|Explicit)\b")


# The job carrying the blocking half of the network leg's assertion --
# "were the 54 collected at all", as opposed to "did they pass", which is
# allowed to go red when someone else's host does. build.yml says why.
DISCOVERY_JOB = "network-discovered"

# The step that runs the six 4xx tests on every push, and the --filter that
# is the only thing keeping the other 88 tests of that suite -- the ones that
# hammer openlibrary.org -- out of the push pipeline. See _AssemblyGate.
API_CONTRACT_STEP = "Run the 4xx contract tests"
API_CONTRACT_CATEGORY = "ApiContract"


def jobs_section(workflow):
    """Everything from `jobs:` on.

    `on:` and `concurrency:` have two-space children too, so job lookups
    that did not start here could match `push:` as though it were a job.
    """
    start = workflow.find("\njobs:\n")

    return workflow if start < 0 else workflow[start:]


def job_block(workflow, job_id):
    """The text of one job, from its two-space `id:` line to the next job."""
    section = jobs_section(workflow)
    start = section.find("\n  " + job_id + ":\n")

    if start < 0:
        return None

    end = re.search(r"\n  [A-Za-z0-9_-]+:\n", section[start + 1:])

    return section[start:] if end is None else section[start:start + 1 + end.start()]


def step_block(workflow, name):
    """The text of one workflow step, from its `- name:` to the next."""
    start = workflow.find("- name: " + name + "\n")

    if start < 0:
        return None

    end = workflow.find("\n      - name: ", start + 1)

    return workflow[start:] if end < 0 else workflow[start:end]


def excluded_categories(text):
    return set(re.findall(r"Category!=(\w+)", text))


def unit_suite(workflow):
    step = step_block(workflow, "Run unit tests with coverage")
    problems = []

    if step is None or "./test.sh Linux Unit" not in step:
        problems.append("build.yml: the 'Run unit tests with coverage' step no longer runs ./test.sh Linux Unit")

    with open(TEST_SH, encoding="utf-8") as f:
        # LINUX is only excluded on the Windows leg.
        excluded = excluded_categories(f.read()) - {"LINUX"}

    return excluded, problems


def playwright_suite(workflow):
    step = step_block(workflow, "Playwright — no network (blocking)")
    problems = []

    if step is None:
        return set(), ["build.yml: the 'Playwright — no network (blocking)' step is gone"]

    if "READARR_RUN_PLAYWRIGHT: '1'" not in step:
        problems.append("build.yml: the blocking Playwright step no longer sets READARR_RUN_PLAYWRIGHT: '1', so every test is ignored")

    if "LogFilePath=PlaywrightResult.xml" not in step:
        problems.append("build.yml: the blocking Playwright step no longer writes PlaywrightResult.xml")

    if "continue-on-error" in step:
        problems.append("build.yml: the blocking Playwright step has continue-on-error, so it no longer blocks")

    return excluded_categories(step), problems


def network_suite(workflow):
    step = step_block(workflow, "Run integration-category tests")
    problems = []

    if step is None or "./test.sh Linux Integration Test" not in step:
        problems.append("build.yml: the 'Run integration-category tests' step no longer runs ./test.sh Linux Integration Test")

    job = job_block(workflow, DISCOVERY_JOB)

    if job is None:
        problems.append("build.yml: the '{0}' job is gone, so nothing blocks on the network tests being collected".format(DISCOVERY_JOB))
    else:
        # Job-level only: the artifact download inside it is deliberately
        # continue-on-error, and that one must not read as this one.
        if re.search(r"\n    continue-on-error:", job):
            problems.append("build.yml: the '{0}' job has continue-on-error, so it no longer blocks".format(DISCOVERY_JOB))

        if "if: always()" not in job:
            problems.append("build.yml: the '{0}' job is no longer `if: always()`, so a network leg that died would skip it -- and branch protection reads a skipped job as success".format(DISCOVERY_JOB))

    with open(TEST_SH, encoding="utf-8") as f:
        # LINUX is only excluded on the Windows leg. Integration ADDS
        # Category=IntegrationTest rather than excluding it, so the
        # exclusions that matter are the shared ones.
        excluded = excluded_categories(f.read()) - {"LINUX", "IntegrationTest", "AutomationTest"}

    return excluded, problems


def api_contract_suite(workflow):
    step = step_block(workflow, API_CONTRACT_STEP)

    if step is None:
        return set(), ["build.yml: the '{0}' step is gone".format(API_CONTRACT_STEP)]

    problems = []

    if "READARR_RUN_INTEGRATION: '1'" not in step:
        problems.append("build.yml: the '{0}' step no longer sets READARR_RUN_INTEGRATION: '1', so _AssemblyGate ignores every test in it".format(API_CONTRACT_STEP))

    # The dangerous direction. Without the filter, READARR_RUN_INTEGRATION=1
    # turns all 94 tests loose on openlibrary.org on every push -- which is
    # the thing _AssemblyGate exists to prevent, and the one way landing this
    # job could have weakened it.
    if "TestCategory=" + API_CONTRACT_CATEGORY not in step:
        problems.append("build.yml: the '{0}' step no longer filters to TestCategory={1}, so READARR_RUN_INTEGRATION=1 would run the whole integration suite against live OpenLibrary on every push".format(API_CONTRACT_STEP, API_CONTRACT_CATEGORY))

    if "LogFilePath=$PWD/ApiContractResult.xml" not in step:
        problems.append("build.yml: the '{0}' step no longer writes ApiContractResult.xml, so the check below reads nothing".format(API_CONTRACT_STEP))

    return excluded_categories(step), problems


# Each list, the CI step that must run its tests, and the arguments every
# assert_tests_ran.py invocation that reads it must still be called with. A
# list missing here is an error, so a new list cannot be added without
# saying where it runs.
# saying where it runs. The third element is the category its step SELECTS
# the tests by, where the step uses one -- losing that attribute drops a test
# from CI as completely as deleting it, and leaves the entry looking healthy.
SUITES = {
    "unit.txt": (unit_suite, ["TestResult.xml .github/required-tests/unit.txt"], None),
    "playwright.txt": (playwright_suite, ["PlaywrightResult.xml .github/required-tests/playwright.txt"], None),
    # Two, because this check is split by blame: the discovered half blocks
    # in its own job, the passed half only reports. Losing either one is a
    # silent hole, so both are named here.
    "network.txt": (network_suite, [
        "--discovered-only TestResult.xml .github/required-tests/network.txt",
        "--passed-only TestResult.xml .github/required-tests/network.txt",
    ], None),
    # --only is not optional decoration: it is the runtime half of keeping
    # the other 88 tests of that suite off the push pipeline.
    "api-contract.txt": (api_contract_suite, [
        "--only ApiContractResult.xml .github/required-tests/api-contract.txt",
    ], API_CONTRACT_CATEGORY),
}


def load_sources():
    """(path, text, namespace) for every C# file under src/."""
    sources = []

    for folder, dirs, files in os.walk(os.path.join(ROOT, "src")):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj", "node_modules")]

        for name in files:
            if not name.endswith(".cs"):
                continue

            path = os.path.join(folder, name)

            with open(path, encoding="utf-8-sig", errors="replace") as f:
                text = f.read()

            match = re.search(r"^\s*namespace\s+([\w.]+)", text, re.MULTILINE)

            if match:
                sources.append((path, text, match.group(1)))

    return sources


def attributes_before(text, index):
    """The attribute lines directly above a declaration."""
    # Drop the declaration's own line: its indentation, or "public " before
    # "class".
    lines = text[:index].rsplit("\n", 1)[0].splitlines()
    attributes = []

    for line in reversed(lines):
        stripped = line.strip()

        if stripped.startswith("["):
            attributes.append(stripped)
        elif stripped.startswith("//"):
            continue
        else:
            break

    return "\n".join(attributes)


def class_body(text, start):
    """From a class declaration to its closing brace."""
    depth = 0
    opened = False

    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
            opened = True
        elif text[i] == "}":
            depth -= 1

            if opened and depth == 0:
                return text[start:i + 1]

    return text[start:]


def categories(attributes):
    # [Category("X")], and the CategoryAttribute subclasses in
    # NzbDrone.Test.Common/Categories, whose category is their own name:
    # [IntegrationTest] is Category=IntegrationTest.
    named = set(re.findall(r"Category\s*\(\s*\"([^\"]+)\"", attributes))
    bare = set(re.findall(r"[\[,]\s*(\w+)\s*(?=[\],(])", attributes))

    return named | bare


def find_test(sources, entry):
    """(class attributes, class body, method name or None) for an entry."""
    parts = entry.split(".")

    for cut in (1, 2):
        if len(parts) <= cut:
            continue

        namespace = ".".join(parts[:-cut])
        class_name = parts[-cut]
        method = parts[-1] if cut == 2 else None

        for path, text, ns in sources:
            if ns != namespace:
                continue

            match = re.search(r"\bclass\s+" + re.escape(class_name) + r"\b", text)

            if match:
                return path, attributes_before(text, match.start()), class_body(text, match.start()), method

    return None


def check_entry(sources, entry, minimum, excluded, list_name, selected_by=None):
    found = find_test(sources, entry)
    where = "{0}: {1}".format(list_name, entry)

    if found is None:
        return ["{0}: no such fixture or test in src/ (renamed or deleted?)".format(where)]

    path, class_attributes, body, method = found
    path = os.path.relpath(path, ROOT)
    problems = []

    if DISABLED_ATTRIBUTE.search(class_attributes):
        problems.append("{0}: the fixture in {1} is marked [Ignore] or [Explicit]".format(where, path))

    blocked = categories(class_attributes) & excluded

    if blocked:
        problems.append("{0}: the fixture's category {1} is filtered out of its CI step".format(where, ", ".join(sorted(blocked))))

    if method is None:
        if selected_by and selected_by not in categories(class_attributes):
            problems.append('{0}: the fixture in {1} has no [Category("{2}")], so its CI step\'s filter no longer selects it'.format(where, path, selected_by))

        count = len(TEST_ATTRIBUTE.findall(body))

        if count < minimum:
            problems.append("{0}: {1} has {2} test attribute(s), the list requires at least {3}".format(where, path, count, minimum))

        return problems

    match = re.search(r"\b(?:public|internal|private|protected)[^;{=(]*\b" + re.escape(method) + r"\s*\(", body)

    if match is None:
        return problems + ["{0}: no method {1} in {2} (renamed or deleted?)".format(where, method, path)]

    method_attributes = attributes_before(body, match.start())

    if not TEST_ATTRIBUTE.search(method_attributes):
        problems.append("{0}: {1} is no longer a [Test]".format(where, method))

    if DISABLED_ATTRIBUTE.search(method_attributes):
        problems.append("{0}: {1} is marked [Ignore] or [Explicit]".format(where, method))

    blocked = categories(method_attributes) & excluded

    if blocked:
        problems.append("{0}: category {1} is filtered out of its CI step".format(where, ", ".join(sorted(blocked))))

    if selected_by and selected_by not in categories(method_attributes) | categories(class_attributes):
        problems.append('{0}: no [Category("{1}")] on {2} or its fixture, so its CI step\'s filter no longer selects it'.format(where, selected_by, method))

    return problems


def main():
    with open(WORKFLOW, encoding="utf-8") as f:
        workflow = f.read()

    problems = []
    sources = load_sources()
    lists = sorted(n for n in os.listdir(LIST_DIR) if n.endswith(".txt"))

    for list_name in lists:
        if list_name not in SUITES:
            problems.append("{0}: no CI step is configured for this list in scripts/check_required_tests_wired.py".format(list_name))
            continue

        suite, invocations, selected_by = SUITES[list_name]
        excluded, suite_problems = suite(workflow)
        problems.extend(suite_problems)

        for arguments in invocations:
            invocation = "scripts/assert_tests_ran.py " + arguments

            if invocation not in workflow:
                problems.append("build.yml: nothing runs `{0}` any more".format(invocation))

        with open(os.path.join(LIST_DIR, list_name), encoding="utf-8") as f:
            required = parse_required(f.read())

        if not required:
            problems.append("{0}: the list is empty".format(list_name))

        for entry, minimum in required:
            problems.extend(check_entry(sources, entry, minimum, excluded, list_name, selected_by))

    for name in SUITES:
        if name not in lists:
            problems.append(".github/required-tests/{0} is missing".format(name))

    for problem in problems:
        print(problem)

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
