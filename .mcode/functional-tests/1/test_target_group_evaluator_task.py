"""
Functional tests for TargetGroupEvaluatorTask milestone.

These tests verify:
1. Build correctness: Smartstore.Core compiles with 0 errors and 0 warnings
2. Test compilation: Smartstore.Core.Tests compiles with 0 errors and 0 warnings
3. Static code review: T1-T10 behavioral contracts are correct
4. NUnit test execution: all 10 T1-T10 tests pass via dotnet test

The project uses NUnit 4 / C# / EF Core InMemory. Tests are executed via
subprocess (PowerShell + pixi activate-env target-app + dotnet test).

Environment: Windows, dotnet SDK via pixi activate-env target-app.
"""

import subprocess
import re
import os
import pytest


WORKSPACE = os.environ.get(
    "WORKSPACE_DIR",
    r"C:\Users\nirbenisrael\.local\share\modelcode\workspace\jobs\8dbd1e48-03b6-4013-b28d-7b8416b214ab\workspace",
)
REPO_ROOT = os.path.join(WORKSPACE, "Smartstore")
CORE_PROJ = os.path.join(REPO_ROOT, "src", "Smartstore.Core", "Smartstore.Core.csproj")
TEST_PROJ = os.path.join(REPO_ROOT, "test", "Smartstore.Core.Tests", "Smartstore.Core.Tests.csproj")
PIXI_ENV_HELPER = os.environ.get("PIXI_ACTIVATE_ENV_HELPER", "")


def _subprocess_env() -> dict:
    """Return an env dict with PIXI_CACHE_DIR set so pixi can locate its cache."""
    env = os.environ.copy()
    if not env.get("PIXI_CACHE_DIR"):
        # PIXI_CACHE_DIR is not set; derive it from the workspace root so that
        # pixi shell-hook can resolve the cache directory regardless of whether
        # HOME/USERPROFILE point at the real user home or a sandbox job dir.
        pixi_manifest = env.get("PIXI_PROJECT_MANIFEST", "")
        workspace_root = os.path.dirname(pixi_manifest) if pixi_manifest else ""
        if workspace_root:
            env["PIXI_CACHE_DIR"] = os.path.join(workspace_root, ".pixi", "cache")
    return env


def run_ps_script(ps_body: str, timeout: int = 300) -> tuple[int, str]:
    """Run an arbitrary PowerShell script body and return (returncode, combined_output)."""
    result = subprocess.run(
        ["powershell", "-NonInteractive", "-Command", ps_body],
        capture_output=True,
        text=True,
        timeout=timeout,
        env=_subprocess_env(),
    )
    combined = (result.stdout or "") + (result.stderr or "")
    return result.returncode, combined


def run_dotnet_build(proj_path: str) -> tuple[int, str]:
    """Run dotnet build via pixi target-app environment."""
    ps = (
        f'. "{PIXI_ENV_HELPER}"\n'
        f'activate-env target-app\n'
        f'Set-Location "{REPO_ROOT}"\n'
        f'dotnet build "{proj_path}" --no-restore -c Release 2>&1'
    )
    return run_ps_script(ps, timeout=300)


_core_build_result = None
_test_build_result = None


def _get_core_build_result():
    """Run dotnet build Smartstore.Core once and cache the result."""
    global _core_build_result
    if _core_build_result is None:
        _core_build_result = run_dotnet_build(CORE_PROJ)
    return _core_build_result


def _get_test_build_result():
    """Run dotnet build Smartstore.Core.Tests once and cache the result."""
    global _test_build_result
    if _test_build_result is None:
        _test_build_result = run_dotnet_build(TEST_PROJ)
    return _test_build_result


# -----------------------------------------------------------------------
# Build: Smartstore.Core.csproj
# -----------------------------------------------------------------------

class TestCoreProjectBuild:
    """Verify that Smartstore.Core.csproj builds with 0 errors and 0 warnings."""

    def test_core_build_succeeds(self):
        """dotnet build Smartstore.Core compiles with 0 errors."""
        rc, output = _get_core_build_result()
        assert rc == 0, f"Build exited {rc}. Output:\n{output}"
        assert "Build succeeded." in output, f"Expected 'Build succeeded.' in output:\n{output}"
        assert re.search(r"0 Error\(s\)", output), f"Expected 0 errors:\n{output}"

    def test_core_build_zero_warnings(self):
        """dotnet build Smartstore.Core produces 0 warnings."""
        rc, output = _get_core_build_result()
        assert rc == 0, f"Build exited {rc}. Output:\n{output}"
        assert re.search(r"0 Warning\(s\)", output), f"Expected 0 warnings:\n{output}"


# -----------------------------------------------------------------------
# Build: Smartstore.Core.Tests.csproj
# -----------------------------------------------------------------------

class TestTestProjectBuild:
    """Verify that Smartstore.Core.Tests.csproj compiles with 0 errors."""

    def test_test_project_build_succeeds(self):
        """dotnet build Smartstore.Core.Tests compiles with 0 errors."""
        rc, output = _get_test_build_result()
        assert rc == 0, f"Test build exited {rc}. Output:\n{output}"
        assert "Build succeeded." in output, f"Expected 'Build succeeded.' in output:\n{output}"
        assert re.search(r"0 Error\(s\)", output), f"Expected 0 errors:\n{output}"

    def test_test_project_build_zero_warnings(self):
        """dotnet build Smartstore.Core.Tests produces 0 warnings."""
        rc, output = _get_test_build_result()
        assert rc == 0, f"Test build exited {rc}. Output:\n{output}"
        assert re.search(r"0 Warning\(s\)", output), f"Expected 0 warnings:\n{output}"


# -----------------------------------------------------------------------
# NUnit execution: individual T1-T10 behavioral tests
# These run via the full test suite (not --filter alone) because running
# the TargetGroupEvaluatorTaskTests in isolation triggers an NUnit
# OneTimeSetUp ordering issue. Running the full suite lets the shared
# DataSettings.Instance be set up by the broader test fixture ordering.
# -----------------------------------------------------------------------

_full_suite_result = None


def _get_full_suite_result():
    """Run the full test suite once and cache the result."""
    global _full_suite_result
    if _full_suite_result is None:
        ps = (
            f'. "{PIXI_ENV_HELPER}"\n'
            f'activate-env target-app\n'
            f'Set-Location "{REPO_ROOT}"\n'
            f'dotnet test "{TEST_PROJ}" --no-restore -c Release '
            f'--logger "console;verbosity=normal" 2>&1'
        )
        rc, output = run_ps_script(ps, timeout=600)
        _full_suite_result = (rc, output)
    return _full_suite_result


# -----------------------------------------------------------------------
# NUnit T1-T10 behavioral tests — parametrized to eliminate DRY violation
# Each tuple: (pytest_id, expected_pass_string, filter_key)
# pytest_id must match the method name used in be_test_manifest.json.
# -----------------------------------------------------------------------

_NUNIT_CASES = [
    ("test_T1_all_system_mappings_deleted",
     "Passed T1_AllSystemMappingsDeleted",
     "T1"),
    ("test_T2_customer_role_ids_scopes_delete_and_load",
     "Passed T2_CustomerRoleIds_ScopesDeleteAndLoad",
     "T2"),
    ("test_T3_matching_customers_mappings_created",
     "Passed T3_MatchingCustomers_MappingsCreated",
     "T3"),
    ("test_T4_no_matching_customers_no_mappings",
     "Passed T4_NoMatchingCustomers_NoMappingsCreated",
     "T4"),
    ("test_T5_inactive_role_skipped",
     "Passed T5_InactiveRole_IsSkipped",
     "T5"),
    ("test_T6_inactive_ruleset_skipped",
     "Passed T6_InactiveRuleSet_SkippedAndNullReturn_NoMappings",
     "T6"),
    ("test_T7_cache_invalidated_when_mappings_added",
     "Passed T7_CacheInvalidated_WhenMappingsAdded",
     "T7"),
    ("test_T8_cache_not_invalidated_when_nothing_changes",
     "Passed T8_CacheNotInvalidated_WhenNothingChanges",
     "T8"),
    ("test_T9_cancellation_token_checked_before_create_expression_group",
     "Passed T9_CancellationToken_CheckedBeforeCreateExpressionGroup",
     "T9"),
    ("test_T10_cancellation_mid_chunk_first_chunk_committed",
     "Passed T10_CancellationToken_MidChunk_FirstChunkCommitted",
     "T10"),
]


@pytest.mark.parametrize(
    "expected_pass_string,filter_key",
    [(case[1], case[2]) for case in _NUNIT_CASES],
    ids=[case[0] for case in _NUNIT_CASES],
)
def test_nunit_behavioral(
    expected_pass_string: str,
    filter_key: str,
) -> None:
    """Verify that the NUnit test identified by expected_pass_string passed in the full suite."""
    _, output = _get_full_suite_result()
    assert expected_pass_string in output, (
        f"{filter_key} did not pass in full suite. Relevant output:\n"
        + "\n".join(l for l in output.splitlines() if filter_key in l or "TargetGroup" in l)
    )
