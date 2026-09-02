/**
 * Auth setup — logs in as the admin user and saves the session storage state
 * to admin-storage-state.json so admin-gated spec files can reuse it.
 *
 * Login flow observed by the QA agent:
 *   1. GET /login/ — extract __RequestVerificationToken from the hidden input
 *      (note: index 0 is the search form; login form is index 1)
 *   2. POST /login/ with UsernameOrEmail, Password, CustomerLoginType=UsernameOrEmail
 *      and the token
 *   3. On success the server returns HTTP 302 → / ; session cookie is set.
 */

import { test as setup, expect } from "@playwright/test";
import path from "path";

export const ADMIN_STORAGE_STATE = path.join(
  __dirname,
  "admin-storage-state.json"
);

setup("authenticate as admin", async ({ page }) => {
  await page.goto("http://localhost:5000/login/");

  // The page has two forms: [0] search, [1] login.
  // Pick up the CSRF token from the login form specifically.
  const loginForm = page.locator("form").nth(1);
  await expect(loginForm).toBeVisible();

  // Fill credentials
  await page.fill("#UsernameOrEmail", "admin@mystore.com");
  await page.fill("#Password", "admin_123456");

  // Submit and wait for navigation away from /login/
  await Promise.all([
    page.waitForURL((url) => !url.pathname.includes("/login"), {
      timeout: 15000,
    }),
    loginForm.locator('button[type="submit"]').click(),
  ]);

  // Persist the authenticated session
  await page.context().storageState({ path: ADMIN_STORAGE_STATE });
});
