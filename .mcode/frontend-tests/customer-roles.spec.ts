/**
 * Customer roles admin page tests — verify the roles used by
 * TargetGroupEvaluatorTask are present and the page loads correctly.
 *
 * QA observations (http://localhost:5000/admin/customerrole/list):
 *   - Page heading: "Customer roles" in a <span> inside <div class="title">
 *   - "Add new..." button at top
 *   - Data table columns: Name, System name, Active, Tax exempt, Free shipping,
 *     Is system role
 *   - 4 system roles: Administrators, Forum Moderators, Guests, Registered
 *   - Pagination: "Displaying items 1-4 of 4"
 *   - Table root selector: .datagrid-root
 *   - Each role name appears twice in the grid: as a link <a> and a cell div.
 *     Use .first() to avoid strict-mode violations.
 *
 * Relevance: these are the customer roles the TargetGroupEvaluatorTask
 * evaluates. Their presence confirms the role data the task works against is
 * intact.
 */

import { test, expect } from "@playwright/test";

async function loginAsAdmin(page: import("@playwright/test").Page) {
  await page.goto("http://localhost:5000/login/");
  await page.fill("#UsernameOrEmail", "admin@mystore.com");
  await page.fill("#Password", "admin_123456");
  await Promise.all([
    page.waitForNavigation({ timeout: 20000 }),
    page.evaluate(() => {
      (document.querySelectorAll("form")[1] as HTMLFormElement).submit();
    }),
  ]);
}

test.describe("Customer Roles Admin Page", () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("http://localhost:5000/admin/customerrole/list");
    // Wait for the data grid to render
    await page.waitForSelector(".datagrid-root", { timeout: 15000 });
  });

  test("should display the Customer roles heading", async ({ page }) => {
    // QA: heading is in <div class="title"><span>Customer roles</span></div>
    await expect(
      page.locator("div.title span", { hasText: "Customer roles" })
    ).toBeVisible({ timeout: 15000 });
  });

  test("should display all 4 system roles in the data grid", async ({
    page,
  }) => {
    const grid = page.locator(".datagrid-root");
    // QA: 4 system roles present. Each name appears twice (link + cell div).
    // Use .first() to pick the first match and avoid strict-mode violations.
    for (const roleName of [
      "Administrators",
      "Forum Moderators",
      "Guests",
      "Registered",
    ]) {
      await expect(grid.getByText(roleName).first()).toBeVisible({
        timeout: 10000,
      });
    }
  });

  test("should show the Administrators system role", async ({ page }) => {
    const grid = page.locator(".datagrid-root");
    await expect(grid.getByText("Administrators").first()).toBeVisible();
  });

  test("should show the Guests system role", async ({ page }) => {
    const grid = page.locator(".datagrid-root");
    await expect(grid.getByText("Guests").first()).toBeVisible();
  });

  test("should show the Registered system role", async ({ page }) => {
    const grid = page.locator(".datagrid-root");
    await expect(grid.getByText("Registered").first()).toBeVisible();
  });

  test("should show the Forum Moderators system role", async ({ page }) => {
    const grid = page.locator(".datagrid-root");
    await expect(grid.getByText("Forum Moderators").first()).toBeVisible();
  });

  test("should display the Add new button", async ({ page }) => {
    // QA: "Add new..." button at top of page
    await expect(
      page.locator("a, button", { hasText: /Add new/i })
    ).toBeVisible();
  });

  test("should display pagination showing 4 items", async ({ page }) => {
    // QA: "Displaying items 1-4 of 4"
    await expect(page.locator("text=/Displaying items 1-4 of 4/i")).toBeVisible(
      { timeout: 10000 }
    );
  });
});
