#!/usr/bin/env python3
"""Pre-commit check that the required regression tests are still wired up.

CI proves the tests in .github/required-tests/*.txt ran and passed, by
reading the result files (assert_tests_ran.py). That only says so after a
push. This catches the same breakages before the commit, statically, in
well under a second:

  - a required test's fixture or method no longer exists (renamed, deleted)
  - it, or its fixture, is marked [Ignore] or [Explicit]
  - it carries a category its CI step filters out
  - the CI step no longer un-gates the suite or no longer runs the check
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


# Each list, the CI step that must run its tests, and the result file the
# check reads. A list missing here is an error, so a new list cannot be
# added without saying where it runs.
SUITES = {
    "unit.txt": (unit_suite, "TestResult.xml"),
    "playwright.txt": (playwright_suite, "PlaywrightResult.xml"),
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


def check_entry(sources, entry, minimum, excluded, list_name):
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

        suite, results_file = SUITES[list_name]
        excluded, suite_problems = suite(workflow)
        problems.extend(suite_problems)

        invocation = "scripts/assert_tests_ran.py {0} .github/required-tests/{1}".format(results_file, list_name)

        if invocation not in workflow:
            problems.append("build.yml: nothing runs `{0}` any more".format(invocation))

        with open(os.path.join(LIST_DIR, list_name), encoding="utf-8") as f:
            required = parse_required(f.read())

        if not required:
            problems.append("{0}: the list is empty".format(list_name))

        for entry, minimum in required:
            problems.extend(check_entry(sources, entry, minimum, excluded, list_name))

    for name in SUITES:
        if name not in lists:
            problems.append(".github/required-tests/{0} is missing".format(name))

    for problem in problems:
        print(problem)

    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
