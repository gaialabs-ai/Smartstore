/**
 * Homepage tests — verify the Smartstore storefront loads correctly.
 *
 * QA observations (http://localhost:5000/):
 *   - Smartstore logo + wordmark in top-left
 *   - Top nav: currency selector (EUR), LOG IN link, SERVICE dropdown
 *   - Cookie consent modal appears on first visit; "Accept all" button present
 *   - After accepting: <h1>Welcome to our store</h1>
 *   - Footer: newsletter email input (#newsletter-email), social media icons,
 *     Themes selector (Flex / Flex Black / Flex Blue)
 */

import { test, expect } from "@playwright/test";

test.describe("Homepage", () => {
  test("should return HTTP 200 and display the page title", async ({
    page,
  }) => {
    const response = await page.goto("http://localhost:5000/");
    expect(response?.status()).toBe(200);
    // QA: page title is "Shop"
    await expect(page).toHaveTitle(/Shop/i);
  });

  test("should show the cookie consent modal on first visit", async ({
    page,
  }) => {
    await page.goto("http://localhost:5000/");
    // QA: cookie dialog has "Accept all" button
    const acceptAll = page.locator("button", { hasText: "Accept all" });
    await expect(acceptAll).toBeVisible({ timeout: 10000 });
  });

  test("should display the store heading after accepting cookies", async ({
    page,
  }) => {
    await page.goto("http://localhost:5000/");

    // Accept cookies if the modal is present
    const acceptAll = page.locator("button", { hasText: "Accept all" });
    if (await acceptAll.isVisible({ timeout: 5000 }).catch(() => false)) {
      await acceptAll.click();
      await page.waitForTimeout(500);
    }

    // QA: <h1>Welcome to our store</h1>
    await expect(page.locator("h1", { hasText: "Welcome to our store" })).toBeVisible();
  });

  test("should render the LOG IN navigation link", async ({ page }) => {
    await page.goto("http://localhost:5000/");
    // QA: top nav has a "LOG IN" link (the page has two matching — use first)
    await expect(page.locator("a", { hasText: /Log in/i }).first()).toBeVisible();
  });

  test("should render the newsletter email input in the footer", async ({
    page,
  }) => {
    await page.goto("http://localhost:5000/");
    // QA observed selector: input#newsletter-email
    await expect(page.locator("#newsletter-email")).toBeVisible();
  });

  test("should render the theme selector with Flex options in the footer", async ({
    page,
  }) => {
    await page.goto("http://localhost:5000/");
    // QA: a <select> with options Flex / Flex Black / Flex Blue
    const themeSelect = page.locator("select").filter({
      has: page.locator("option", { hasText: "Flex" }),
    });
    await expect(themeSelect).toBeVisible();
  });
});
