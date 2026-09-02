"""
Functional test: Verify the NUnit test suite for TargetGroupEvaluatorTask
passes completely, confirming behavioral parity with the legacy implementation.
"""
import subprocess
import os
import re
import pytest

WORKSPACE_DIR = os.environ.get("WORKSPACE_DIR", "/l2l/workspace")
REPO_DIR = os.path.join(WORKSPACE_DIR, "Smartstore")
TEST_PROJECT = os.path.join(
    REPO_DIR, "test", "Smartstore.Core.Tests", "Smartstore.Core.Tests.csproj"
)
TEST_FILTER = "FullyQualifiedName~TargetGroupEvaluatorTaskTests"


def run_dotnet_test(filter_expr=None):
    """Run dotnet test and return the subprocess result."""
    cmd = [
        "dotnet", "test", TEST_PROJECT,
        "-c", "Release",
        "--no-build",
        "--logger", "console;verbosity=detailed",
    ]
    if filter_expr:
        cmd.extend(["--filter", filter_expr])
    return subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        timeout=120,
        cwd=REPO_DIR,
    )


class TestTargetGroupEvaluatorTaskSuite:
    """Verify all 22 NUnit tests in TargetGroupEvaluatorTaskTests pass."""

    def test_all_22_tests_discovered(self):
        """The test runner discovers exactly 22 test cases."""
        result = run_dotnet_test(TEST_FILTER)
        assert result.returncode == 0, (
            f"Test run failed with exit code {result.returncode}.\n"
            f"stdout: {result.stdout[-2000:]}\n"
            f"stderr: {result.stderr[-2000:]}"
        )
        # Check discovery count
        match = re.search(
            r"discovered (\d+) of \d+ NUnit test cases", result.stdout
        )
        assert match is not None, "Could not find test discovery count in output."
        discovered = int(match.group(1))
        assert discovered == 22, (
            f"Expected 22 tests discovered, got {discovered}."
        )

    def test_all_tests_pass(self):
        """All 22 TargetGroupEvaluatorTask tests pass (0 failed)."""
        result = run_dotnet_test(TEST_FILTER)
        assert result.returncode == 0, (
            f"Test run failed with exit code {result.returncode}.\n"
            f"stderr: {result.stderr[-2000:]}"
        )
        assert "Test Run Successful" in result.stdout, (
            "Test run did not report 'Test Run Successful'."
        )
        # Parse pass count
        match = re.search(r"Passed:\s+(\d+)", result.stdout)
        assert match is not None, "Could not parse passed test count."
        passed = int(match.group(1))
        assert passed == 22, f"Expected 22 passed, got {passed}."

    def test_no_test_failures(self):
        """No test failures in the TargetGroupEvaluatorTask test suite."""
        result = run_dotnet_test(TEST_FILTER)
        assert "Failed:" not in result.stdout or "Failed:     0" in result.stdout, (
            "Some tests failed.\n" + result.stdout[-2000:]
        )


class TestDeleteScopeTests:
    """Verify the delete scope dimension tests pass individually."""

    def test_delete_all_system_mappings(self):
        """Run_WithNoCustomerRoleIds_DeletesAllSystemMappings passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_WithNoCustomerRoleIds_DeletesAllSystemMappings"
        )
        assert result.returncode == 0
        assert "Passed Run_WithNoCustomerRoleIds_DeletesAllSystemMappings" in result.stdout

    def test_delete_scoped_system_mappings(self):
        """Run_WithCustomerRoleIds_DeletesOnlyScopedSystemMappings passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_WithCustomerRoleIds_DeletesOnlyScopedSystemMappings"
        )
        assert result.returncode == 0
        assert "Passed Run_WithCustomerRoleIds_DeletesOnlyScopedSystemMappings" in result.stdout

    def test_manual_mappings_preserved(self):
        """Run_ManualMappingsArePreserved passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_ManualMappingsArePreserved"
        )
        assert result.returncode == 0
        assert "Passed Run_ManualMappingsArePreserved" in result.stdout

    def test_empty_customer_role_ids(self):
        """Run_EmptyCustomerRoleIds_ResultsInNoDeletions passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_EmptyCustomerRoleIds_ResultsInNoDeletions"
        )
        assert result.returncode == 0
        assert "Passed Run_EmptyCustomerRoleIds_ResultsInNoDeletions" in result.stdout


class TestRuleEvaluationMappingTests:
    """Verify rule evaluation and mapping dimension tests pass individually."""

    def test_matching_customers_receive_mappings(self):
        """Run_MatchingCustomers_ReceiveSystemMappings passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_MatchingCustomers_ReceiveSystemMappings"
        )
        assert result.returncode == 0
        assert "Passed Run_MatchingCustomers_ReceiveSystemMappings" in result.stdout

    def test_non_matching_customers_no_mappings(self):
        """Run_NonMatchingCustomers_ReceiveNoMappings passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_NonMatchingCustomers_ReceiveNoMappings"
        )
        assert result.returncode == 0
        assert "Passed Run_NonMatchingCustomers_ReceiveNoMappings" in result.stdout

    def test_multiple_rulesets_union(self):
        """Run_MultipleRuleSetsPerRole_CustomerIdsAreUnioned passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_MultipleRuleSetsPerRole_CustomerIdsAreUnioned"
        )
        assert result.returncode == 0
        assert "Passed Run_MultipleRuleSetsPerRole_CustomerIdsAreUnioned" in result.stdout

    def test_multiple_roles_correct_associations(self):
        """Run_MultipleRoles_CorrectRoleToCustomerAssociations passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_MultipleRoles_CorrectRoleToCustomerAssociations"
        )
        assert result.returncode == 0
        assert "Passed Run_MultipleRoles_CorrectRoleToCustomerAssociations" in result.stdout

    def test_inactive_ruleset_skipped(self):
        """Run_InactiveRuleSet_IsSkipped passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_InactiveRuleSet_IsSkipped"
        )
        assert result.returncode == 0
        assert "Passed Run_InactiveRuleSet_IsSkipped" in result.stdout


class TestCacheInvalidationTests:
    """Verify cache invalidation dimension tests pass individually."""

    def test_cache_cleared_on_add(self):
        """Run_WhenMappingsAdded_ClearsAclCache passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_WhenMappingsAdded_ClearsAclCache"
        )
        assert result.returncode == 0
        assert "Passed Run_WhenMappingsAdded_ClearsAclCache" in result.stdout

    def test_cache_cleared_on_delete(self):
        """Run_WhenMappingsDeleted_ClearsAclCache passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_WhenMappingsDeleted_ClearsAclCache"
        )
        assert result.returncode == 0
        assert "Passed Run_WhenMappingsDeleted_ClearsAclCache" in result.stdout

    def test_no_cache_clear_when_nothing_changed(self):
        """Run_WhenNothingChanged_DoesNotClearCache passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_WhenNothingChanged_DoesNotClearCache"
        )
        assert result.returncode == 0
        assert "Passed Run_WhenNothingChanged_DoesNotClearCache" in result.stdout


class TestCancellationTests:
    """Verify cancellation dimension tests pass individually."""

    def test_cancellation_during_rule_evaluation(self):
        """Run_CancellationDuringRuleSetEvaluation_StopsEarly passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_CancellationDuringRuleSetEvaluation_StopsEarly"
        )
        assert result.returncode == 0
        assert "Passed Run_CancellationDuringRuleSetEvaluation_StopsEarly" in result.stdout

    def test_cancellation_during_chunk_insertion(self):
        """Run_CancellationDuringChunkInsertion_StopsEarly passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_CancellationDuringChunkInsertion_StopsEarly"
        )
        assert result.returncode == 0
        assert "Passed Run_CancellationDuringChunkInsertion_StopsEarly" in result.stdout


class TestEdgeCaseTests:
    """Verify edge case dimension tests pass individually."""

    def test_no_active_roles(self):
        """Run_NoActiveRolesWithRuleSets_NoMappingsNoCache passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_NoActiveRolesWithRuleSets_NoMappingsNoCache"
        )
        assert result.returncode == 0
        assert "Passed Run_NoActiveRolesWithRuleSets_NoMappingsNoCache" in result.stdout

    def test_inactive_role_skipped(self):
        """Run_InactiveRole_IsSkipped passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_InactiveRole_IsSkipped"
        )
        assert result.returncode == 0
        assert "Passed Run_InactiveRole_IsSkipped" in result.stdout

    def test_non_filter_expression_skipped(self):
        """Run_ExpressionGroupIsNotFilterExpression_RuleSetSkipped passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_ExpressionGroupIsNotFilterExpression_RuleSetSkipped"
        )
        assert result.returncode == 0
        assert "Passed Run_ExpressionGroupIsNotFilterExpression_RuleSetSkipped" in result.stdout

    def test_customer_role_ids_scope_both_phases(self):
        """Run_CustomerRoleIdsScope_AppliesToBothDeleteAndEvaluate passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_CustomerRoleIdsScope_AppliesToBothDeleteAndEvaluate"
        )
        assert result.returncode == 0
        assert "Passed Run_CustomerRoleIdsScope_AppliesToBothDeleteAndEvaluate" in result.stdout

    def test_zero_matching_customers(self):
        """Run_RoleWithActiveRuleSetMatchingZeroCustomers_NoMappingsForThatRole passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_RoleWithActiveRuleSetMatchingZeroCustomers_NoMappingsForThatRole"
        )
        assert result.returncode == 0
        assert "Passed Run_RoleWithActiveRuleSetMatchingZeroCustomers" in result.stdout

    def test_role_with_no_rulesets(self):
        """Run_RoleWithNoRuleSets_IsNotIncludedInQuery passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_RoleWithNoRuleSets_IsNotIncludedInQuery"
        )
        assert result.returncode == 0
        assert "Passed Run_RoleWithNoRuleSets_IsNotIncludedInQuery" in result.stdout

    def test_large_customer_set_chunked(self):
        """Run_LargeCustomerSet_IsChunkedCorrectly passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_LargeCustomerSet_IsChunkedCorrectly"
        )
        assert result.returncode == 0
        assert "Passed Run_LargeCustomerSet_IsChunkedCorrectly" in result.stdout

    def test_progress_reported_for_each_role(self):
        """Run_ProgressReported_ForEachRole passes."""
        result = run_dotnet_test(
            "FullyQualifiedName~Run_ProgressReported_ForEachRole"
        )
        assert result.returncode == 0
        assert "Passed Run_ProgressReported_ForEachRole" in result.stdout


class TestFullTestSuite:
    """Verify the entire test suite (all projects) passes."""

    def test_full_suite_passes(self):
        """All tests across all test projects pass."""
        solution_path = os.path.join(REPO_DIR, "Smartstore.sln")
        result = subprocess.run(
            [
                "dotnet", "test", solution_path,
                "-c", "Release",
                "--no-build",
                "--logger", "console;verbosity=normal",
            ],
            capture_output=True,
            text=True,
            timeout=300,
            cwd=REPO_DIR,
        )
        assert result.returncode == 0, (
            f"Full test suite failed with exit code {result.returncode}.\n"
            f"stderr: {result.stderr[-2000:]}"
        )
        # Verify no failures
        assert "Failed:     0" not in result.stdout or "Failed!" not in result.stdout, (
            "Some tests failed in the full suite."
        )
