import { test, expect } from "@playwright/test";

// Tests for the Smartstore installation wizard page.
// The app runs in wizard mode (no database configured), so /install is the
// only accessible page. Selectors and observed values sourced from the QA
// report (qa-report.md) — origin and target are byte-for-byte identical.

test.describe("Install Wizard — Page Load and Redirect", () => {
  test("root URL redirects to /install", async ({ page }) => {
    // The root path must redirect (HTTP 302) to the install wizard.
    await page.goto("/", { waitUntil: "networkidle" });
    // After following the redirect, the final URL should be /install.
    expect(page.url()).toContain("/install");
  });

  test("install page returns HTTP 200", async ({ page }) => {
    const response = await page.goto("/install");
    expect(response?.status()).toBe(200);
  });
});

test.describe("Install Wizard — Header and Title", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/install");
  });

  test("page title contains 'Installation'", async ({ page }) => {
    await expect(page).toHaveTitle(/Installation/i);
  });

  test("page has no h1 — 'Installation' appears in page title only", async ({ page }) => {
    // The install wizard does not render an h1. "Installation" is the <title>.
    // Verify: no h1 present, and title contains "Installation".
    await expect(page.locator("h1")).toHaveCount(0);
    await expect(page).toHaveTitle(/Installation/i);
  });

  test("Smartstore logo or header is visible", async ({ page }) => {
    // The blue header is always present; it contains either an img (logo) or
    // the product name.  The QA screenshot shows a blue header region.
    const header = page.locator("header, .navbar, nav, #header, .install-header");
    // At least one header-like element should exist on the page.
    await expect(header.first()).toBeVisible();
  });
});

test.describe("Install Wizard — Store Information Section", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/install");
  });

  test("admin email field is present with default value", async ({ page }) => {
    // QA report: textbox "Admin user email", default value admin@mystore.com
    const emailField = page.getByRole("textbox", { name: /admin user email/i });
    await expect(emailField).toBeVisible();
    await expect(emailField).toHaveValue("admin@mystore.com");
  });

  test("admin password field is present and empty", async ({ page }) => {
    // QA report: textbox "Admin user password", empty
    const passwordField = page.getByRole("textbox", { name: /admin user password/i });
    await expect(passwordField).toBeVisible();
    await expect(passwordField).toHaveValue("");
  });

  test("confirm password field is present and empty", async ({ page }) => {
    // QA report: textbox "Confirm the password", empty
    const confirmField = page.getByRole("textbox", { name: /confirm the password/i });
    await expect(confirmField).toBeVisible();
    await expect(confirmField).toHaveValue("");
  });
});

test.describe("Install Wizard — Database Information Section", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/install");
  });

  test("database system dropdown defaults to SQL Server Express", async ({ page }) => {
    // QA report: combobox "Database system", default "Microsoft SQL Server (Express)"
    const dbDropdown = page.getByRole("combobox", { name: /database system/i });
    await expect(dbDropdown).toBeVisible();
    // Verify the selected option text contains "SQL Server"
    const selectedLabel = await dbDropdown.evaluate(
      (el: HTMLSelectElement) => el.options[el.selectedIndex]?.textContent ?? ""
    );
    expect(selectedLabel).toContain("SQL Server");
  });

  test("connection type dropdown is present", async ({ page }) => {
    // QA report: combobox "Connection", default "Enter connection values"
    const connectionDropdown = page.getByRole("combobox", { name: /^connection$/i });
    await expect(connectionDropdown).toBeVisible();
  });

  test("server name textbox is present", async ({ page }) => {
    // QA report: textbox with placeholder "Server name"
    const serverField = page.getByPlaceholder(/server name/i);
    await expect(serverField).toBeVisible();
  });

  test("database name textbox is present", async ({ page }) => {
    // QA report: textbox with placeholder "Database name"
    const dbNameField = page.getByPlaceholder(/database name/i);
    await expect(dbNameField).toBeVisible();
  });

  test("custom collation checkbox is present and unchecked", async ({ page }) => {
    // QA report: checkbox "Use custom collation", unchecked by default
    const collationCheckbox = page.getByRole("checkbox", { name: /use custom collation/i });
    await expect(collationCheckbox).toBeVisible();
    await expect(collationCheckbox).not.toBeChecked();
  });
});

test.describe("Install Wizard — Options Section", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/install");
  });

  test("primary language dropdown defaults to English", async ({ page }) => {
    // QA report: combobox "Primary language", default "English"
    const langDropdown = page.getByRole("combobox", { name: /primary language/i });
    await expect(langDropdown).toBeVisible();
    // Verify the selected option text contains "English"
    const selectedLabel = await langDropdown.evaluate(
      (el: HTMLSelectElement) => el.options[el.selectedIndex]?.textContent ?? ""
    );
    expect(selectedLabel).toContain("English");
  });

  test("media storage dropdown is present", async ({ page }) => {
    // QA report: combobox "Media storage", default "File system (recommended)"
    const storageDropdown = page.getByRole("combobox", { name: /media storage/i });
    await expect(storageDropdown).toBeVisible();
  });

  test("create sample data checkbox is present and unchecked", async ({ page }) => {
    // QA report: checkbox "Create sample data", unchecked by default
    const sampleDataCheckbox = page.getByRole("checkbox", { name: /create sample data/i });
    await expect(sampleDataCheckbox).toBeVisible();
    await expect(sampleDataCheckbox).not.toBeChecked();
  });
});

test.describe("Install Wizard — Install Button", () => {
  test.beforeEach(async ({ page }) => {
    await page.goto("/install");
  });

  test("Install button is present and visible", async ({ page }) => {
    // QA report: button:has-text("Install"), present on the page.
    // The button contains an icon <i> + <span>Install</span>; Playwright's
    // accessible name includes surrounding whitespace, so we use a substring
    // match rather than an anchored regex.
    const installButton = page.getByRole("button", { name: /install/i });
    await expect(installButton).toBeVisible();
  });

  test("admin email field accepts user input", async ({ page }) => {
    const emailField = page.getByRole("textbox", { name: /admin user email/i });
    await emailField.fill("test@example.com");
    await expect(emailField).toHaveValue("test@example.com");
  });

  test("password fields accept user input", async ({ page }) => {
    const passwordField = page.getByRole("textbox", { name: /admin user password/i });
    const confirmField = page.getByRole("textbox", { name: /confirm the password/i });
    await passwordField.fill("TestPassword123!");
    await confirmField.fill("TestPassword123!");
    await expect(passwordField).toHaveValue("TestPassword123!");
    await expect(confirmField).toHaveValue("TestPassword123!");
  });
});
