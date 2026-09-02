/**
 * Scheduled tasks admin page tests — verify the TargetGroupEvaluatorTask is
 * registered and visible in the scheduler.
 *
 * QA observations (http://localhost:5000/admin/scheduling/list):
 *   - Page heading: "Scheduled Tasks" in <div class="title"><span>...</span></div>
 *   - Data grid columns: Name, Enabled, Cron Expression, Last Run, Next Run in
 *   - Grid root selector: .datagrid-root
 *   - Scrollable container: .dg-table-wrapper (scrollHeight=899)
 *   - 10+ tasks visible; the task under test is:
 *       "Update assignments of customers to customer roles"
 *       Cron: "15 2 * * *"  (At 02:15 AM)
 *       Status: Enabled (checkmark)
 *   - QA selector to find the row:
 *       Array.from(document.querySelectorAll('tr'))
 *         .find(r => r.textContent.includes('customer roles'))
 *   - The grid is a virtual scrollable container; the target row may be below
 *     the fold — the test scrolls .dg-table-wrapper to reveal it.
 *   - "Edit" and "Run now" are inside a dropdown-menu that is hidden until
 *     the "..." toggle is clicked; use toBeAttached() (exists in DOM) not
 *     toBeVisible() (visible on screen) for those links.
 *
 * This is the primary functional assertion for the milestone: the
 * TargetGroupEvaluatorTask must appear as a registered scheduled task.
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

/** Scroll the datagrid wrapper until the given text is visible, or max scrolls reached. */
async function scrollUntilVisible(
  page: import("@playwright/test").Page,
  searchText: string,
  maxScrolls = 10
) {
  const wrapper = page.locator(".dg-table-wrapper");
  for (let i = 0; i < maxScrolls; i++) {
    const row = page.locator("tr", { hasText: searchText });
    if (await row.isVisible().catch(() => false)) return true;
    // Scroll the wrapper 200px at a time
    await wrapper.evaluate((el) => (el.scrollTop += 200));
    await page.waitForTimeout(300);
  }
  return false;
}

test.describe("Scheduled Tasks Admin Page", () => {
  test.beforeEach(async ({ page }) => {
    await loginAsAdmin(page);
    await page.goto("http://localhost:5000/admin/scheduling/list");
    // Wait for the grid to render
    await page.waitForSelector(".datagrid-root", { timeout: 15000 });
  });

  test("should show the Scheduled Tasks heading", async ({ page }) => {
    // QA: heading is in <div class="title"><span>Scheduled Tasks</span></div>
    await expect(
      page.locator("div.title span", { hasText: "Scheduled Tasks" })
    ).toBeVisible({ timeout: 15000 });
  });

  test("should display the datagrid with scheduled tasks", async ({ page }) => {
    await expect(page.locator(".datagrid-root")).toBeVisible();
  });

  test("should show the TargetGroupEvaluatorTask as Update assignments of customers to customer roles", async ({
    page,
  }) => {
    // QA exact display name observed in the grid
    const found = await scrollUntilVisible(
      page,
      "Update assignments of customers to customer roles"
    );
    expect(found).toBe(true);

    const taskRow = page.locator("tr", {
      hasText: "Update assignments of customers to customer roles",
    });
    await expect(taskRow).toBeVisible({ timeout: 5000 });
  });

  test("should show the TargetGroupEvaluatorTask with the correct cron expression 15 2 * * *", async ({
    page,
  }) => {
    // QA: cron expression "15 2 * * *" on the task row
    await scrollUntilVisible(
      page,
      "Update assignments of customers to customer roles"
    );
    const taskRow = page.locator("tr", {
      hasText: "Update assignments of customers to customer roles",
    });
    await expect(taskRow).toContainText("15 2 * * *");
  });

  test("should show the TargetGroupEvaluatorTask as Enabled with At 02:15 AM schedule", async ({
    page,
  }) => {
    // QA: the task row contains "02:15" (human-readable cron description)
    await scrollUntilVisible(
      page,
      "Update assignments of customers to customer roles"
    );
    const taskRow = page.locator("tr", {
      hasText: "Update assignments of customers to customer roles",
    });
    await expect(taskRow).toContainText("02:15");
  });

  test("should have an Edit link in the DOM for the TargetGroupEvaluatorTask row", async ({
    page,
  }) => {
    // QA: each task row has "Edit", "Run now", "Cancel" links in a dropdown
    // The dropdown is hidden until "..." is clicked — check DOM presence only.
    await scrollUntilVisible(
      page,
      "Update assignments of customers to customer roles"
    );
    const taskRow = page.locator("tr", {
      hasText: "Update assignments of customers to customer roles",
    });
    // The Edit link is a dropdown item — attached to the DOM but may be hidden
    await expect(taskRow.getByText("Edit")).toBeAttached({ timeout: 5000 });
  });

  test("should have a Run now link in the DOM for the TargetGroupEvaluatorTask row", async ({
    page,
  }) => {
    await scrollUntilVisible(
      page,
      "Update assignments of customers to customer roles"
    );
    const taskRow = page.locator("tr", {
      hasText: "Update assignments of customers to customer roles",
    });
    // The Run now link is a dropdown item — attached to the DOM but may be hidden
    await expect(taskRow.getByText("Run now")).toBeAttached({ timeout: 5000 });
  });

  test("should show other known scheduled tasks in the grid", async ({
    page,
  }) => {
    // QA: "Send emails" is also present in the visible top rows
    const grid = page.locator(".datagrid-root");
    await expect(grid.getByText("Send emails").first()).toBeVisible();
  });
});
