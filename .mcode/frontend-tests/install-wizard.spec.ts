import { test, expect } from "@playwright/test";

/**
 * Smoke tests for the Smartstore target build.
 *
 * Background: This milestone adds a NUnit test suite for TargetGroupEvaluatorTask
 * (backend changes only). The web application is running in full-shop mode with a
 * SQLite database configured in Settings.txt. These tests confirm the app renders
 * correctly after the backend-only changes.
 *
 * QA report verdict: PAIR install-wizard-initial: match
 * Origin and target app pages are visually identical. No frontend changes were made.
 *
 * Note: Settings.txt is present in the repo with a SQLite connection, so the app
 * starts in full-shop mode rather than install-wizard mode. The /install route
 * redirects to the shop homepage.
 */

test.describe("Smartstore shop — smoke tests", () => {
  test("homepage loads with HTTP 200", async ({ page }) => {
    const response = await page.goto("/");
    expect(response?.status()).toBe(200);
  });

  test("page title is 'Shop'", async ({ page }) => {
    await page.goto("/");
    await expect(page).toHaveTitle("Shop");
  });

  test("/install route redirects to shop homepage", async ({ page }) => {
    // With a configured database, /install redirects to the main shop
    const response = await page.goto("/install");
    // After following the redirect, the final page should be HTTP 200
    expect(response?.status()).toBe(200);
    // And it should land on the shop (not the install wizard)
    await expect(page).toHaveTitle("Shop");
  });

  test("Smartstore store name link is visible in header", async ({ page }) => {
    await page.goto("/");
    // Store name link in the header — "Your store name" is the default store name
    const storeName = page.getByRole("link", { name: "Your store name" });
    await expect(storeName).toBeVisible();
  });

  test("currency selector is visible in header", async ({ page }) => {
    await page.goto("/");
    // Currency combobox shows the current currency
    const currencySelector = page.getByRole("combobox", {
      name: /current currency/i,
    });
    await expect(currencySelector).toBeVisible();
  });

  test("header navigation links are visible", async ({ page }) => {
    await page.goto("/");
    // Scope to the #header element to avoid matching footer duplicates
    const header = page.locator("#header");
    await expect(
      header.getByRole("link", { name: /contact us/i }).first()
    ).toBeVisible();
    await expect(header.getByRole("link", { name: /log in/i })).toBeVisible();
  });

  test("search box is visible", async ({ page }) => {
    await page.goto("/");
    const searchBox = page.getByRole("combobox", {
      name: /what are you looking for/i,
    });
    await expect(searchBox).toBeVisible();
  });

  test("wishlist and basket links are visible", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("link", { name: "Wishlist" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Basket" })).toBeVisible();
  });

  test("'Welcome to our store' heading is visible", async ({ page }) => {
    await page.goto("/");
    await expect(
      page.getByRole("heading", { name: "Welcome to our store", level: 1 })
    ).toBeVisible();
  });

  test("footer contains Smartstore branding", async ({ page }) => {
    await page.goto("/");
    // Footer should contain "SmartStore AG" branding
    await expect(page.locator("footer, #footer")).toContainText(
      /SmartStore AG/i
    );
  });

  test("footer navigation sections are visible", async ({ page }) => {
    await page.goto("/");
    // Three standard footer nav sections from the QA-observed page
    await expect(
      page.getByRole("navigation", { name: "Information" })
    ).toBeVisible();
    await expect(
      page.getByRole("navigation", { name: "Service" })
    ).toBeVisible();
    await expect(
      page.getByRole("navigation", { name: "Company" })
    ).toBeVisible();
  });

  test("newsletter subscription form is visible", async ({ page }) => {
    await page.goto("/");
    await expect(
      page.getByRole("heading", { name: "Subscribe to newsletters" })
    ).toBeVisible();
    // Email textbox inside the newsletter region
    await expect(page.getByRole("textbox", { name: "Email" })).toBeVisible();
  });
});
