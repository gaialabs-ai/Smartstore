"""
Functional test: Verify the Smartstore solution builds successfully
and the TargetGroupEvaluatorTask compiles without errors.

The full solution build uses the lifecycle script (which applies a
ModuleBuilder workaround). We also verify the core and test projects
compile independently.
"""
import subprocess
import os
import pytest

WORKSPACE_DIR = os.environ.get("WORKSPACE_DIR", "/l2l/workspace")
REPO_DIR = os.path.join(WORKSPACE_DIR, "Smartstore")

CORE_PROJECT = os.path.join(
    REPO_DIR, "src", "Smartstore.Core", "Smartstore.Core.csproj"
)
TEST_PROJECT = os.path.join(
    REPO_DIR, "test", "Smartstore.Core.Tests", "Smartstore.Core.Tests.csproj"
)


class TestBuildCompilation:
    """Verify the Smartstore projects compile and the target task has no errors."""

    def test_core_project_builds_successfully(self):
        """dotnet build Smartstore.Core.csproj -c Release succeeds (contains the task)."""
        result = subprocess.run(
            ["dotnet", "build", CORE_PROJECT, "-c", "Release", "--no-restore"],
            capture_output=True,
            text=True,
            timeout=300,
            cwd=REPO_DIR,
        )
        assert result.returncode == 0, (
            f"Core project build failed with exit code {result.returncode}.\n"
            f"stderr: {result.stderr[-2000:]}\n"
            f"stdout (last 2000): {result.stdout[-2000:]}"
        )
        assert "Build succeeded" in result.stdout, (
            "Build output does not contain 'Build succeeded'."
        )

    def test_core_tests_project_builds_successfully(self):
        """dotnet build Smartstore.Core.Tests.csproj -c Release succeeds."""
        result = subprocess.run(
            ["dotnet", "build", TEST_PROJECT, "-c", "Release", "--no-restore"],
            capture_output=True,
            text=True,
            timeout=300,
            cwd=REPO_DIR,
        )
        assert result.returncode == 0, (
            f"Test project build failed with exit code {result.returncode}.\n"
            f"stderr: {result.stderr[-2000:]}\n"
            f"stdout (last 2000): {result.stdout[-2000:]}"
        )
        assert "Build succeeded" in result.stdout, (
            "Build output does not contain 'Build succeeded'."
        )

    def test_no_build_warnings_or_errors_in_target_task(self):
        """The TargetGroupEvaluatorTask.cs file compiles without warnings or errors."""
        result = subprocess.run(
            ["dotnet", "build", CORE_PROJECT, "-c", "Release", "--no-restore"],
            capture_output=True,
            text=True,
            timeout=300,
            cwd=REPO_DIR,
        )
        task_file = "TargetGroupEvaluatorTask.cs"
        output = result.stdout + result.stderr
        warning_lines = [
            line for line in output.splitlines()
            if task_file in line and ("warning" in line.lower() or "error" in line.lower())
        ]
        assert len(warning_lines) == 0, (
            f"Build produced warnings/errors for {task_file}:\n"
            + "\n".join(warning_lines)
        )

    def test_zero_build_errors_in_core(self):
        """The core project builds with 0 errors."""
        result = subprocess.run(
            ["dotnet", "build", CORE_PROJECT, "-c", "Release", "--no-restore"],
            capture_output=True,
            text=True,
            timeout=300,
            cwd=REPO_DIR,
        )
        assert "0 Error(s)" in result.stdout, (
            "Build did not report '0 Error(s)' in output."
        )
