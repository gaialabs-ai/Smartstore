"""
Functional tests for the TargetGroupEvaluatorTask via the
POST /admin/customerrole/ApplyRules/{id} endpoint.

The TargetGroupEvaluatorTask is a background scheduled task that evaluates
rule-based customer segmentation. It can be triggered on-demand via the
admin controller's ApplyRules action for specific customer roles.

These tests verify that:
1. The endpoint is accessible with admin authentication
2. The task executes successfully for valid customer role IDs
3. The endpoint handles non-existent role IDs correctly
4. The endpoint requires authentication (rejects unauthenticated requests)
"""

import re
import time
import urllib.parse

import pytest
import requests

BASE_URL = "http://localhost:5000"
ADMIN_EMAIL = "admin@test.com"
ADMIN_PASSWORD = "Admin123!"


@pytest.fixture(scope="module")
def admin_session():
    """Create an authenticated admin session.

    Logs in as admin and returns a requests.Session with the auth cookie set.
    This fixture is module-scoped so all tests in this module share one login.
    """
    s = requests.Session()

    # First hit the home page to initialize cookies (CookieConsent, etc.)
    home_resp = s.get(f"{BASE_URL}/", timeout=30)
    assert home_resp.status_code == 200, (
        f"Home page returned {home_resp.status_code}"
    )

    # Get login page and extract antiforgery token
    login_page = s.get(f"{BASE_URL}/login/", timeout=30)
    assert login_page.status_code == 200, (
        f"Login page returned {login_page.status_code}"
    )

    match = re.search(
        r'__RequestVerificationToken.*?value="([^"]+)"', login_page.text
    )
    assert match, "Could not find __RequestVerificationToken on login page"
    token = match.group(1)

    # Submit login form
    login_data = {
        "UsernameOrEmail": ADMIN_EMAIL,
        "Password": ADMIN_PASSWORD,
        "__RequestVerificationToken": token,
        "RememberMe": "false",
    }
    login_resp = s.post(
        f"{BASE_URL}/login/",
        data=login_data,
        timeout=30,
        allow_redirects=False,
    )
    assert login_resp.status_code == 302, (
        f"Login POST returned {login_resp.status_code}, expected 302 redirect"
    )

    # Verify identity cookie was set
    cookie_names = [c.name for c in s.cookies]
    assert ".Smart.Identity" in cookie_names, (
        f"No .Smart.Identity cookie after login. Cookies: {cookie_names}"
    )

    yield s
    s.close()


def get_rvt_token(session, url):
    """Extract the request verification token from an admin page."""
    page = session.get(url, timeout=30)
    assert page.status_code == 200, f"Page {url} returned {page.status_code}"
    match = re.search(r"__rvt.*?content='([^']+)'", page.text)
    if not match:
        match = re.search(
            r'__RequestVerificationToken.*?value="([^"]+)"', page.text
        )
    assert match, f"Could not find antiforgery token on {url}"
    return match.group(1)


def post_with_antiforgery(session, url, rvt, **kwargs):
    """POST to an admin endpoint with the X-XSRF-Token header."""
    headers = kwargs.pop("headers", {})
    headers["X-Requested-With"] = "XMLHttpRequest"
    headers["X-XSRF-Token"] = urllib.parse.quote(rvt)
    return session.post(url, headers=headers, timeout=60, **kwargs)


@pytest.fixture(autouse=True)
def health_check():
    """Verify the app is reachable before each test."""
    try:
        resp = requests.get(f"{BASE_URL}/", timeout=10)
        assert resp.status_code == 200
    except requests.ConnectionError:
        pytest.fail("Application is not reachable at " + BASE_URL)


class TestApplyRulesHappyPath:
    """POST /admin/customerrole/ApplyRules/{id} -- valid role IDs."""

    def test_apply_rules_administrators_role(self, admin_session):
        """Apply rules for the Administrators role (ID=1).

        The task should execute successfully. The endpoint schedules the
        TargetGroupEvaluatorTask via RunSingleTaskAsync with CustomerRoleIds=[1]
        and returns the role edit page (200). Since no active rule sets are
        configured for this role, the task will:
        1. Delete any existing system mappings for role ID 1
        2. Find no active roles with active rule sets to process
        3. Return a success response with the role edit page
        """
        rvt = get_rvt_token(
            admin_session, f"{BASE_URL}/admin/customerrole/list"
        )
        resp = post_with_antiforgery(
            admin_session,
            f"{BASE_URL}/admin/customerrole/ApplyRules/1",
            rvt,
        )
        assert resp.status_code == 200, (
            f"ApplyRules(1) returned {resp.status_code}: {resp.text[:500]}"
        )
        # The endpoint returns the customer role edit page after scheduling the task
        assert "Administrators" in resp.text, (
            "Response should contain the Administrators role edit page"
        )

    def test_apply_rules_registered_role(self, admin_session):
        """Apply rules for the Registered role (ID=3).

        Similar to the Administrators test -- the task runs but since this role
        has no active rule sets, it does a scoped delete and returns.
        """
        rvt = get_rvt_token(
            admin_session, f"{BASE_URL}/admin/customerrole/list"
        )
        resp = post_with_antiforgery(
            admin_session,
            f"{BASE_URL}/admin/customerrole/ApplyRules/3",
            rvt,
        )
        assert resp.status_code == 200, (
            f"ApplyRules(3) returned {resp.status_code}: {resp.text[:500]}"
        )

    def test_apply_rules_guests_role(self, admin_session):
        """Apply rules for the Guests role (ID=4)."""
        rvt = get_rvt_token(
            admin_session, f"{BASE_URL}/admin/customerrole/list"
        )
        resp = post_with_antiforgery(
            admin_session,
            f"{BASE_URL}/admin/customerrole/ApplyRules/4",
            rvt,
        )
        assert resp.status_code == 200, (
            f"ApplyRules(4) returned {resp.status_code}: {resp.text[:500]}"
        )


class TestApplyRulesNotFound:
    """POST /admin/customerrole/ApplyRules/{id} -- non-existent role."""

    def test_apply_rules_nonexistent_role(self, admin_session):
        """Apply rules for a non-existent role ID should return 404."""
        rvt = get_rvt_token(
            admin_session, f"{BASE_URL}/admin/customerrole/list"
        )
        resp = post_with_antiforgery(
            admin_session,
            f"{BASE_URL}/admin/customerrole/ApplyRules/99999",
            rvt,
        )
        # The controller returns 404 for non-existent role IDs
        assert resp.status_code == 404, (
            f"ApplyRules(99999) returned {resp.status_code}: {resp.text[:500]}"
        )


class TestApplyRulesAuth:
    """POST /admin/customerrole/ApplyRules/{id} -- auth requirements."""

    def test_apply_rules_unauthenticated(self):
        """Unauthenticated request to ApplyRules should be rejected.

        Without auth cookies, the admin endpoint should redirect to login (302)
        or return 401/403.
        """
        resp = requests.post(
            f"{BASE_URL}/admin/customerrole/ApplyRules/1",
            timeout=30,
            allow_redirects=False,
        )
        assert resp.status_code in (302, 401, 403), (
            f"Unauthenticated ApplyRules returned {resp.status_code}, "
            f"expected 302/401/403"
        )
        # If 302, verify redirect goes to login (or install if app not configured)
        if resp.status_code == 302:
            location = resp.headers.get("Location", "")
            assert "login" in location.lower() or "install" in location.lower(), (
                f"302 redirect goes to '{location}', expected login or install page"
            )


class TestApplyRulesInvalidInput:
    """POST /admin/customerrole/ApplyRules -- boundary / invalid cases."""

    def test_apply_rules_zero_id(self, admin_session):
        """Apply rules with ID=0 (invalid) should return 404."""
        rvt = get_rvt_token(
            admin_session, f"{BASE_URL}/admin/customerrole/list"
        )
        resp = post_with_antiforgery(
            admin_session,
            f"{BASE_URL}/admin/customerrole/ApplyRules/0",
            rvt,
        )
        assert resp.status_code == 404, (
            f"ApplyRules(0) returned {resp.status_code}: {resp.text[:500]}"
        )

    def test_apply_rules_negative_id(self, admin_session):
        """Apply rules with negative ID should return 404."""
        rvt = get_rvt_token(
            admin_session, f"{BASE_URL}/admin/customerrole/list"
        )
        resp = post_with_antiforgery(
            admin_session,
            f"{BASE_URL}/admin/customerrole/ApplyRules/-1",
            rvt,
        )
        assert resp.status_code == 404, (
            f"ApplyRules(-1) returned {resp.status_code}: {resp.text[:500]}"
        )
