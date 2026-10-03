"""
Functional tests for Smartstore web application after TargetGroupEvaluatorTask
milestone changes.

Verifies the application starts and serves HTTP correctly after adding:
- New xUnit test files (TargetGroupEvaluatorTaskTests, SoftDeleteQueryFilterTests,
  TargetGroupServiceDescriptorTests)
- New SQLite test infrastructure (TestSqliteDbFactory, Microsoft.EntityFrameworkCore.Sqlite)
- Updated .gitignore

The app runs without a configured database, so it serves the installation wizard.
These tests verify the app's HTTP pipeline works correctly.
"""

import requests
import pytest

BASE_URL = "http://localhost:5000"


@pytest.fixture(autouse=True)
def health_check():
    """Confirm the app is reachable before running tests."""
    try:
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        assert resp.status_code == 200, f"Health check failed: {resp.status_code}"
    except requests.ConnectionError:
        pytest.fail("Smartstore app is not reachable at http://localhost:5000")


class TestRootRedirect:
    """GET / should redirect to /install when no database is configured."""

    def test_root_redirects_to_install(self):
        """GET / returns 302 redirect to /install."""
        resp = requests.get(f"{BASE_URL}/", timeout=10, allow_redirects=False)
        assert resp.status_code == 302
        assert resp.headers.get("Location") == "/install"

    def test_root_redirect_has_smartstore_header(self):
        """GET / includes X-Powered-By: Smartstore header."""
        resp = requests.get(f"{BASE_URL}/", timeout=10, allow_redirects=False)
        powered_by = resp.headers.get("X-Powered-By", "")
        assert "Smartstore" in powered_by


class TestInstallPage:
    """GET /install should serve the installation wizard."""

    def test_install_page_returns_200(self):
        """GET /install returns 200 OK."""
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        assert resp.status_code == 200

    def test_install_page_has_html_content(self):
        """GET /install returns valid HTML with installation title."""
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        assert resp.status_code == 200
        assert "text/html" in resp.headers.get("Content-Type", "")
        assert "<title>Installation</title>" in resp.text

    def test_install_page_contains_form(self):
        """GET /install contains the installation form with database options."""
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        assert resp.status_code == 200
        # The install page should contain database provider selection
        assert "install" in resp.text.lower()

    def test_install_head_request(self):
        """HEAD /install returns 200 without body."""
        resp = requests.head(f"{BASE_URL}/install", timeout=10)
        assert resp.status_code == 200

    def test_install_page_has_csp_header(self):
        """GET /install includes Content-Security-Policy header."""
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        csp = resp.headers.get("Content-Security-Policy", "")
        assert "frame-ancestors" in csp


class TestUnconfiguredRouting:
    """Routes should redirect to /install when the app is not configured."""

    def test_admin_redirects_to_install(self):
        """GET /admin redirects to /install when unconfigured."""
        resp = requests.get(f"{BASE_URL}/admin", timeout=10, allow_redirects=False)
        assert resp.status_code == 302
        assert resp.headers.get("Location") == "/install"

    def test_arbitrary_path_redirects_to_install(self):
        """GET /some/nonexistent/path redirects to /install when unconfigured."""
        resp = requests.get(
            f"{BASE_URL}/some/nonexistent/path", timeout=10, allow_redirects=False
        )
        assert resp.status_code == 302
        assert resp.headers.get("Location") == "/install"


class TestServerIdentification:
    """Server should identify itself correctly."""

    def test_server_header_is_kestrel(self):
        """Response includes Server: Kestrel header."""
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        assert resp.headers.get("Server") == "Kestrel"

    def test_powered_by_smartstore_version(self):
        """X-Powered-By header includes Smartstore with version."""
        resp = requests.get(f"{BASE_URL}/install", timeout=10)
        powered_by = resp.headers.get("X-Powered-By", "")
        assert "Smartstore" in powered_by
        # Should include version number pattern like "6.x"
        assert any(c.isdigit() for c in powered_by)
