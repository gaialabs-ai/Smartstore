"""
Functional tests for the TargetGroupEvaluatorTask.CustomerRoleIds scoping fix.

Fix commit: 3220151fe3208b994c7888ecfff712456081bf79
Baseline (pre-fix) ref: 485b8dac6a554c28c9817a97d1543d2e5d0b9135

TargetGroupEvaluatorTask has no HTTP surface and is not exposed as a standalone CLI
command of the application - it only runs as a scheduled background task, or via an
admin-panel "Apply rules" action inside a fully-installed Smartstore instance (DB +
install wizard + tenant data), which is explicitly out of scope for this environment.
The target lifecycle strategy for this repo is "managed, test-only"
($MCODE_DIR/lifecycle/target/lifecycle.md): no live HTTP server, no database - the
project's own healthcheck/smoke scripts validate behavior exclusively via
`dotnet test --filter`, which is the closest thing this background task has to an
invocable surface in this environment.

Per explicit delegation, functional verification drives the task's REAL `Run(...)`
method end-to-end against a real SQLite-backed SmartDbContext, using the REAL
RuleService/TargetGroupService rule-evaluation pipeline (not mocked) - see
test/Smartstore.Core.Tests/Platform/Identity/Rules/TargetGroupEvaluatorTaskFunctionalTests.cs
(sibling NUnit test class; this pytest module re-invokes it as the real functional
check, mirroring exactly what the project's own healthcheck lifecycle script does).

This module invokes that NUnit test class as a subprocess against:
  - TARGET: this repo at its current (fixed) ref.
  - ORIGIN: a sibling git worktree checked out at the pre-fix baseline ref
    (485b8dac6a554c28c9817a97d1543d2e5d0b9135), with the same NUnit test file and
    SQLite test-infra (TestSqliteDbFactory.cs + SQLite PackageReferences) ported over
    as test-infra-only additions (no application code changes - origin app code is
    immutable under the mutability contract) so the identical test can run unmodified
    against the pre-fix code. Create it with, e.g.:
        git worktree add --detach <WORKSPACE_DIR>/smartstore-baseline 485b8dac6a554c28c9817a97d1543d2e5d0b9135
    then copy TestSqliteDbFactory.cs, this sibling .cs test file, and the SQLite
    <PackageReference>/<None> items from Smartstore.Core.Tests.csproj into it, and
    `dotnet restore && dotnet build` the test project once.

If the baseline worktree is not present, the origin-side assertions are skipped (not
silently passed) and only the target-side result is recorded for that run; the origin
baseline values asserted on the target side are the ones captured during the actual
Pass 1 origin run performed for this functional-testing pass (see ORIGIN_*_SNAPSHOT
below - captured verbatim, not mutated).

Each test function records its own result via write_test_results.py directly - no
batching/abstraction layer hides what is recorded.
"""

import json
import os
import subprocess
import sys
from pathlib import Path

import pytest

THIS_FILE = Path(__file__).resolve()
TARGET_REPO_DIR = THIS_FILE.parents[3]  # .mcode/functional-tests/1/<file> -> repo root
WORKSPACE_DIR = TARGET_REPO_DIR.parent
BASELINE_REPO_DIR = WORKSPACE_DIR / "smartstore-baseline"

TEST_CSPROJ = "test/Smartstore.Core.Tests/Smartstore.Core.Tests.csproj"
SANDBOX_ROOT = Path(os.environ.get("SANDBOX_ROOT", WORKSPACE_DIR.parent))
WRITE_RESULTS = SANDBOX_ROOT / ".claude" / "skills" / "sandbox-app-runner" / "write_test_results.py"

PIXI_HELPER = os.environ.get("PIXI_ACTIVATE_ENV_HELPER")
if not PIXI_HELPER:
    raise RuntimeError(
        "PIXI_ACTIVATE_ENV_HELPER is not set. This functional test module needs it to "
        "activate the pixi 'target-app' environment before running `dotnet test`, the "
        "same convention used by $MCODE_DIR/lifecycle/target/lifecycle_scripts.json "
        "(`. $env:PIXI_ACTIVATE_ENV_HELPER`). Set it to the path of Activate-Env.ps1 for "
        "this sandbox before running this test module."
    )

NUGET_PACKAGES = os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget" / "packages"))

REPO_NAME = "Smartstore"

# Captured verbatim from the actual Pass 1 origin run (baseline ref 485b8dac6) performed
# for this functional-testing pass. Not mutated.
ORIGIN_UNSCOPED_SNAPSHOT = (
    "{\n"
    '  "numDeleted": 0,\n'
    '  "numAdded": 2,\n'
    '  "roleMappings": {\n'
    '    "Role1": [\n'
    '      "CUST-A"\n'
    "    ],\n"
    '    "Role2": [\n'
    '      "CUST-B"\n'
    "    ]\n"
    "  }\n"
    "}"
)

ORIGIN_SCOPED_SNAPSHOT = (
    "{\n"
    '  "numDeleted": 0,\n'
    '  "numAdded": 1,\n'
    '  "roleMappings": {\n'
    '    "OtherRole": [\n'
    '      "CUST-OTHER-NEW",\n'
    '      "CUST-PRE-EXISTING"\n'
    "    ],\n"
    '    "TargetRole": [\n'
    '      "CUST-TARGET"\n'
    "    ]\n"
    "  }\n"
    "}"
)


def _run_nunit_method(repo_dir: Path, method_name: str, timeout: int = 180):
    """
    Invokes the real TargetGroupEvaluatorTask.Run() method via its NUnit functional test
    class (dotnet test --filter), exactly as the project's own healthcheck lifecycle
    script invokes Smartstore.Core.Tests, against the given repo checkout.
    Returns (returncode, stdout, stderr).
    """
    script = (
        f". '{PIXI_HELPER}'\n"
        "activate-env target-app\n"
        f"Set-Location '{repo_dir}'\n"
        "$env:DOTNET_ROLL_FORWARD = 'LatestMajor'\n"
        f"$env:NUGET_PACKAGES = '{NUGET_PACKAGES}'\n"
        f"dotnet test {TEST_CSPROJ} -c Release --no-build "
        f'--filter "FullyQualifiedName~{method_name}" --logger "console;verbosity=detailed"\n'
        "exit $LASTEXITCODE\n"
    )
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-Command", script],
        capture_output=True,
        text=True,
        timeout=timeout,
    )
    return result.returncode, result.stdout, result.stderr


def _extract_snapshot(stdout: str) -> str:
    """
    Extracts the single balanced JSON snapshot object the NUnit test writes to stdout
    (see WriteResult() in TargetGroupEvaluatorTaskFunctionalTests.cs), by brace-counting
    from the first '"numDeleted"' occurrence back to its enclosing '{'.
    """
    marker = stdout.find('"numDeleted"')
    assert marker != -1, f"No JSON snapshot found in test output:\n{stdout}"

    brace_start = stdout.rfind("{", 0, marker)
    assert brace_start != -1, f"No opening brace found before snapshot marker:\n{stdout}"

    depth = 0
    for i in range(brace_start, len(stdout)):
        if stdout[i] == "{":
            depth += 1
        elif stdout[i] == "}":
            depth -= 1
            if depth == 0:
                return stdout[brace_start : i + 1]

    raise AssertionError(f"Unbalanced JSON snapshot in test output:\n{stdout}")


def _invoke_write_results(args):
    """Thin, non-abstracting subprocess call - every flag is supplied explicitly by the caller."""
    cmd = [sys.executable, str(WRITE_RESULTS), *args]
    result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, (
        f"write_test_results.py failed (exit {result.returncode}):\n"
        f"STDOUT:\n{result.stdout}\nSTDERR:\n{result.stderr}"
    )


class TestUnscopedRun:
    """
    TargetGroupEvaluatorTask.Run() with no CustomerRoleIds parameter (nightly scheduler
    path). The CustomerRoleIds fix only changes the SCOPED code path, so behavior here
    must be identical on origin (pre-fix baseline) and target (fixed): two active roles,
    each backed by its own real rule set, both get recomputed correctly against real
    customer rows.
    """

    METHOD = "Run_Unscoped_RealRuleEngine_RecomputesAllActiveRoles"
    TEST_NAME = "test_target_group_evaluator_unscoped_recomputes_all_active_roles"
    DESCRIPTION = (
        "TargetGroupEvaluatorTask.Run() with no CustomerRoleIds recomputes every active "
        "role's real rule-matched customers (unaffected by the CustomerRoleIds scoping fix)"
    )

    def test_origin_baseline_recomputes_all_active_roles(self):
        if not BASELINE_REPO_DIR.exists():
            pytest.skip(
                f"Baseline worktree not present at {BASELINE_REPO_DIR}; origin Pass 1 "
                "must be (re-)established (see module docstring) before this test can run."
            )

        returncode, stdout, stderr = _run_nunit_method(BASELINE_REPO_DIR, self.METHOD)
        snapshot = _extract_snapshot(stdout)
        parsed = json.loads(snapshot)

        assert returncode == 0, f"Expected unscoped run to PASS at baseline:\n{stdout}\n{stderr}"
        assert parsed["roleMappings"] == {"Role1": ["CUST-A"], "Role2": ["CUST-B"]}

        _invoke_write_results(
            [
                "--name", self.TEST_NAME,
                "--category", "HAPPY_PATH",
                "--description", self.DESCRIPTION,
                "--repo-type", "origin",
                "--repo-name", REPO_NAME,
                "--command", "dotnet test",
                "--args", f"--filter FullyQualifiedName~{self.METHOD}",
                "--expected-exit-code", "0",
                "--actual-exit-code", str(returncode),
                "--actual-output", snapshot,
            ]
        )

    def test_target_matches_origin_unscoped_behavior(self):
        returncode, stdout, stderr = _run_nunit_method(TARGET_REPO_DIR, self.METHOD)
        snapshot = _extract_snapshot(stdout)
        parsed = json.loads(snapshot)

        assert returncode == 0, f"Expected unscoped run to PASS on fixed target:\n{stdout}\n{stderr}"
        assert parsed["roleMappings"] == {"Role1": ["CUST-A"], "Role2": ["CUST-B"]}

        _invoke_write_results(
            [
                "--name", self.TEST_NAME,
                "--category", "HAPPY_PATH",
                "--description", self.DESCRIPTION,
                "--repo-type", "target",
                "--repo-name", REPO_NAME,
                "--command", "dotnet test",
                "--args", f"--filter FullyQualifiedName~{self.METHOD}",
                "--expected-exit-code", "0",
                "--actual-exit-code", str(returncode),
                "--actual-output", snapshot,
                "--origin-output", ORIGIN_UNSCOPED_SNAPSHOT,
                "--output-match",
            ]
        )


class TestScopedCustomerRoleIdsRegression:
    """
    THE REGRESSION: TargetGroupEvaluatorTask.Run() scoped to a single CustomerRoleIds
    value (the admin "Apply rules" button for one role) must only delete+recompute
    mappings for that role. Pre-fix (baseline 485b8dac6), the role-selection query
    ignored CustomerRoleIds, so a second active role's own real rule set got evaluated
    too, silently adding a mapping it should never have added. Post-fix (3220151fe),
    the second role's mapping is left exactly as it was before the scoped run.
    """

    METHOD = "Run_ScopedByCustomerRoleIds_RealRuleEngine_OnlyRecomputesTargetedRole"
    TEST_NAME = "test_target_group_evaluator_scoped_customer_role_ids_regression"
    DESCRIPTION = (
        "TargetGroupEvaluatorTask.Run() scoped by CustomerRoleIds must only recompute "
        "the targeted role, not every active role with an active rule set"
    )

    def test_origin_baseline_reproduces_regression(self):
        if not BASELINE_REPO_DIR.exists():
            pytest.skip(
                f"Baseline worktree not present at {BASELINE_REPO_DIR}; origin Pass 1 "
                "must be (re-)established (see module docstring) before this test can run."
            )

        returncode, stdout, stderr = _run_nunit_method(BASELINE_REPO_DIR, self.METHOD)
        snapshot = _extract_snapshot(stdout)
        parsed = json.loads(snapshot)

        # This assertion documents and locks in the KNOWN pre-fix regression: the
        # non-targeted OtherRole incorrectly receives CUST-OTHER-NEW.
        assert returncode != 0, (
            "Expected the scoped-run regression assertion to FAIL at the pre-fix "
            f"baseline (reproducing the known bug), but it passed:\n{stdout}"
        )
        assert sorted(parsed["roleMappings"]["OtherRole"]) == ["CUST-OTHER-NEW", "CUST-PRE-EXISTING"]

        _invoke_write_results(
            [
                "--name", self.TEST_NAME,
                "--category", "HAPPY_PATH",
                "--description", self.DESCRIPTION,
                "--repo-type", "origin",
                "--repo-name", REPO_NAME,
                "--command", "dotnet test",
                "--args", f"--filter FullyQualifiedName~{self.METHOD}",
                "--expected-exit-code", "0",
                "--actual-exit-code", str(returncode),
                "--actual-output", snapshot,
            ]
        )

    def test_target_fixes_regression(self):
        returncode, stdout, stderr = _run_nunit_method(TARGET_REPO_DIR, self.METHOD)
        snapshot = _extract_snapshot(stdout)
        parsed = json.loads(snapshot)

        assert returncode == 0, (
            f"Expected the scoped-run regression assertion to PASS on the fixed target:\n{stdout}\n{stderr}"
        )
        assert parsed["roleMappings"]["OtherRole"] == ["CUST-PRE-EXISTING"]

        _invoke_write_results(
            [
                "--name", self.TEST_NAME,
                "--category", "HAPPY_PATH",
                "--description", self.DESCRIPTION,
                "--repo-type", "target",
                "--repo-name", REPO_NAME,
                "--command", "dotnet test",
                "--args", f"--filter FullyQualifiedName~{self.METHOD}",
                "--expected-exit-code", "0",
                "--actual-exit-code", str(returncode),
                "--actual-output", snapshot,
                "--origin-output", ORIGIN_SCOPED_SNAPSHOT,
                "--no-output-match",
                "--comparison-note", (
                    "Origin (baseline 485b8dac6) reproduces the confirmed pre-fix regression: a "
                    "scoped run for TargetRole also recomputes OtherRole's own real rule set, "
                    "incorrectly adding CUST-OTHER-NEW alongside the pre-existing mapping. Target "
                    "(commit 3220151fe) filters the role-selection query by CustomerRoleIds the same "
                    "way the delete query already was, so OtherRole is never evaluated and keeps "
                    "exactly its pre-existing mapping. This output divergence from origin IS the "
                    "intended, confirmed bug fix - not a target regression."
                ),
            ]
        )
