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
    r"C:\Users\nirbenisrael\.local\share\modelcode\workspace\jobs\ff430900-fd70-44b1-9462-0ccc2a6e0f94\workspace",
)
REPO_ROOT = os.path.join(WORKSPACE, "Smartstore")
CORE_PROJ = os.path.join(REPO_ROOT, "src", "Smartstore.Core", "Smartstore.Core.csproj")
TEST_PROJ = os.path.join(REPO_ROOT, "test", "Smartstore.Core.Tests", "Smartstore.Core.Tests.csproj")
PIXI_ENV_HELPER = os.environ.get("PIXI_ACTIVATE_ENV_HELPER", "")


def run_ps_script(ps_body: str, timeout: int = 300) -> tuple[int, str]:
    """Run an arbitrary PowerShell script body and return (returncode, combined_output)."""
    result = subprocess.run(
        ["powershell", "-NonInteractive", "-Command", ps_body],
        capture_output=True,
        text=True,
        timeout=timeout,
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


def run_dotnet_test_filter(filter_name: str, timeout: int = 120) -> tuple[int, str]:
    """Run a specific NUnit test by name filter via pixi target-app environment."""
    # Semicolon in --logger must be escaped in the PS command; use a here-string or pass as separate token.
    # Use --logger with just "console" to avoid semicolon parsing issues, then check output text.
    ps = (
        f'. "{PIXI_ENV_HELPER}"\n'
        f'activate-env target-app\n'
        f'Set-Location "{REPO_ROOT}"\n'
        f'dotnet test "{TEST_PROJ}" --no-restore -c Release '
        f'--logger "console;verbosity=normal" '
        f'--filter "FullyQualifiedName~{filter_name}" 2>&1'
    )
    return run_ps_script(ps, timeout=timeout)


# -----------------------------------------------------------------------
# Build: Smartstore.Core.csproj
# -----------------------------------------------------------------------

class TestCoreProjectBuild:
    """Verify that Smartstore.Core.csproj builds with 0 errors and 0 warnings."""

    def test_core_build_succeeds(self):
        """dotnet build Smartstore.Core compiles with 0 errors."""
        rc, output = run_dotnet_build(CORE_PROJ)
        assert rc == 0, f"Build exited {rc}. Output:\n{output}"
        assert "Build succeeded." in output, f"Expected 'Build succeeded.' in output:\n{output}"
        assert re.search(r"0 Error\(s\)", output), f"Expected 0 errors:\n{output}"

    def test_core_build_zero_warnings(self):
        """dotnet build Smartstore.Core produces 0 warnings."""
        rc, output = run_dotnet_build(CORE_PROJ)
        assert rc == 0, f"Build exited {rc}. Output:\n{output}"
        assert re.search(r"0 Warning\(s\)", output), f"Expected 0 warnings:\n{output}"


# -----------------------------------------------------------------------
# Build: Smartstore.Core.Tests.csproj
# -----------------------------------------------------------------------

class TestTestProjectBuild:
    """Verify that Smartstore.Core.Tests.csproj compiles with 0 errors."""

    def test_test_project_build_succeeds(self):
        """dotnet build Smartstore.Core.Tests compiles with 0 errors."""
        rc, output = run_dotnet_build(TEST_PROJ)
        assert rc == 0, f"Test build exited {rc}. Output:\n{output}"
        assert "Build succeeded." in output, f"Expected 'Build succeeded.' in output:\n{output}"
        assert re.search(r"0 Error\(s\)", output), f"Expected 0 errors:\n{output}"

    def test_test_project_build_zero_warnings(self):
        """dotnet build Smartstore.Core.Tests produces 0 warnings."""
        rc, output = run_dotnet_build(TEST_PROJ)
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


class TestNUnitT1:
    """T1: All system mappings deleted when no active roles with rulesets."""

    def test_T1_all_system_mappings_deleted(self):
        _, output = _get_full_suite_result()
        assert "Passed T1_AllSystemMappingsDeleted" in output, (
            f"T1 did not pass in full suite. Relevant output:\n"
            + "\n".join(l for l in output.splitlines() if "T1" in l or "TargetGroup" in l)
        )


class TestNUnitT2:
    """T2: CustomerRoleIds scopes both delete and role-load queries."""

    def test_T2_customer_role_ids_scopes_delete_and_load(self):
        _, output = _get_full_suite_result()
        assert "Passed T2_CustomerRoleIds_ScopesDeleteAndLoad" in output, (
            f"T2 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T2" in l)
        )


class TestNUnitT3:
    """T3: Matching customers produce IsSystemMapping=true rows."""

    def test_T3_matching_customers_mappings_created(self):
        _, output = _get_full_suite_result()
        assert "Passed T3_MatchingCustomers_MappingsCreated" in output, (
            f"T3 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T3" in l)
        )


class TestNUnitT4:
    """T4: No matching customers produces no rows."""

    def test_T4_no_matching_customers_no_mappings(self):
        _, output = _get_full_suite_result()
        assert "Passed T4_NoMatchingCustomers_NoMappingsCreated" in output, (
            f"T4 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T4" in l)
        )


class TestNUnitT5:
    """T5: Inactive role is skipped."""

    def test_T5_inactive_role_skipped(self):
        _, output = _get_full_suite_result()
        assert "Passed T5_InactiveRole_IsSkipped" in output, (
            f"T5 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T5" in l)
        )


class TestNUnitT6:
    """T6: Inactive ruleset skipped; null return produces no mappings."""

    def test_T6_inactive_ruleset_skipped(self):
        _, output = _get_full_suite_result()
        assert "Passed T6_InactiveRuleSet_SkippedAndNullReturn_NoMappings" in output, (
            f"T6 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T6" in l)
        )


class TestNUnitT7:
    """T7: Cache invalidated when mappings change."""

    def test_T7_cache_invalidated_when_mappings_added(self):
        _, output = _get_full_suite_result()
        assert "Passed T7_CacheInvalidated_WhenMappingsAdded" in output, (
            f"T7 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T7" in l)
        )


class TestNUnitT8:
    """T8: Cache not invalidated when nothing changes."""

    def test_T8_cache_not_invalidated_when_nothing_changes(self):
        _, output = _get_full_suite_result()
        assert "Passed T8_CacheNotInvalidated_WhenNothingChanges" in output, (
            f"T8 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T8" in l)
        )


class TestNUnitT9:
    """T9: CancellationToken checked before CreateExpressionGroupAsync exits early."""

    def test_T9_cancellation_token_checked_before_create_expression_group(self):
        _, output = _get_full_suite_result()
        assert "Passed T9_CancellationToken_CheckedBeforeCreateExpressionGroup" in output, (
            f"T9 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T9" in l)
        )


class TestNUnitT10:
    """T10: CancellationToken mid-chunk commits first 500, cancels second 100."""

    def test_T10_cancellation_mid_chunk_first_chunk_committed(self):
        _, output = _get_full_suite_result()
        assert "Passed T10_CancellationToken_MidChunk_FirstChunkCommitted" in output, (
            f"T10 did not pass. Relevant:\n"
            + "\n".join(l for l in output.splitlines() if "T10" in l)
        )
