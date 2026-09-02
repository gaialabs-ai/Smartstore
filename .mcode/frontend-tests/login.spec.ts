/**
 * Login page tests — verify the login form renders correctly.
 *
 * QA observations (http://localhost:5000/login/):
 *   - Page title: "Shop. Login"
 *   - Heading: "Sign In"
 *   - Left-column heading: "I am already registered"
 *   - Username/email field: input#UsernameOrEmail (type="text")
 *   - Password field: input#Password (type="password")
 *   - Password toggle: button with text "Show password"
 *   - Remember me checkbox: input#RememberMe
 *   - Forgot password link: a[href="/passwordrecovery/"]
 *   - Login submit: button[type="submit"] with text "Log in"
 *   - Right column: "Not registered yet?" panel with Register link
 *   - CSRF token: input[name="__RequestVerificationToken"]
 *
 * Important: page has two forms. [0] = search form, [1] = login form.
 */

import { test, expect } from "@playwright/test";

test.describe("Login Page", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("http://localhost:5000/login/");
  });

  test("should return HTTP 200 and display the Sign In heading", async ({
    page,
  }) => {
    const response = await page.goto("http://localhost:5000/login/");
    expect(response?.status()).toBe(200);
    await expect(page.locator("h1, h2, h3", { hasText: "Sign In" })).toBeVisible();
  });

  test("should display the page title containing Login", async ({ page }) => {
    // QA: "Shop. Login"
    await expect(page).toHaveTitle(/Login/i);
  });

  test("should display the UsernameOrEmail input field", async ({ page }) => {
    // QA selector: input#UsernameOrEmail
    const emailField = page.locator("#UsernameOrEmail");
    await expect(emailField).toBeVisible();
    await expect(emailField).toHaveAttribute("type", "text");
  });

  test("should display the Password input field", async ({ page }) => {
    // QA selector: input#Password
    const passwordField = page.locator("#Password");
    await expect(passwordField).toBeVisible();
    await expect(passwordField).toHaveAttribute("type", "password");
  });

  test("should display the Show password toggle button", async ({ page }) => {
    // QA: button with aria-label="Show password" (uses icon, not visible text)
    await expect(
      page.locator('button[aria-label="Show password"]')
    ).toBeAttached();
  });

  test("should display the Remember me checkbox", async ({ page }) => {
    // QA selector: input#RememberMe
    await expect(page.locator("#RememberMe")).toBeVisible();
  });

  test("should display the Forgot password link", async ({ page }) => {
    // QA selector: a[href="/passwordrecovery/"]
    await expect(page.locator('a[href="/passwordrecovery/"]')).toBeVisible();
  });

  test("should display the Log in submit button", async ({ page }) => {
    // QA: button[type="submit"] with text "Log in" inside the login form (form index 1)
    const loginForm = page.locator("form").nth(1);
    await expect(
      loginForm.locator('button[type="submit"]', { hasText: /Log in/i })
    ).toBeVisible();
  });

  test("should include a CSRF token hidden input", async ({ page }) => {
    // QA: hidden input[name="__RequestVerificationToken"]
    // The page has multiple forms (search, login, newsletter) so there are
    // multiple CSRF tokens in the DOM — just verify at least one exists.
    const csrfInput = page.locator(
      'input[name="__RequestVerificationToken"]'
    );
    const count = await csrfInput.count();
    expect(count).toBeGreaterThanOrEqual(1);
  });

  test("should display the Register link for new users", async ({ page }) => {
    // QA: right column has "Not registered yet?" with a Register link
    await expect(
      page.locator("a", { hasText: /Register/i })
    ).toBeVisible();
  });
});
