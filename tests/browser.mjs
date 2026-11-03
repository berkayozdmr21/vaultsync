import { chromium } from "@playwright/test";
import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
await mkdir(".runtime/screenshots", { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const errors = [];
page.on("pageerror", (e) => errors.push(e.message));
try {
  await page.goto("http://127.0.0.1:9250/");
  await page.getByLabel("Erişim anahtarı").fill("vaultsync-local-dev");
  await page.getByRole("button", { name: "Çalışma alanına gir" }).click();
  await page.getByRole("heading", { name: "Dosyaların, güvende." }).waitFor();
  await page.screenshot({
    path: ".runtime/screenshots/overview.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Sürümler", exact: false }).click();
  await page
    .getByRole("button", { name: "İncele", exact: false })
    .first()
    .click();
  const fileLinks = page.locator(".manifest a");
  const initialCount = await fileLinks.count();
  assert.ok(initialCount > 0);
  const firstPath = (await fileLinks.first().getAttribute("href")).split(
    "path=",
  )[1];
  const search = page.getByRole("searchbox", { name: "Bu sürümde dosya ara" });
  await search.fill(decodeURIComponent(firstPath));
  assert.equal(await fileLinks.count(), 1);
  await search.fill("no-matching-file-xyz");
  await page
    .getByRole("status")
    .filter({ hasText: "Bu aramayla eşleşen dosya bulunamadı." })
    .waitFor();
  assert.equal(await fileLinks.count(), 0);
  await search.fill("");
  assert.equal(await fileLinks.count(), initialCount);
  await page
    .getByRole("button", { name: "Önceki sürümle karşılaştır" })
    .click();
  await page.locator(".diff").waitFor();
  await page
    .getByRole("button", { name: "Bütünlüğü doğrula", exact: true })
    .click();
  await page.getByRole("button", { name: "Bütünlük doğrulandı" }).waitFor();
  await page.screenshot({
    path: ".runtime/screenshots/version.png",
    fullPage: true,
  });
  await page.getByRole("button", { name: "Sürümü kapat" }).click();
  await page.getByRole("button", { name: "Depolama", exact: true }).click();
  await page.getByRole("button", { name: "Önizlemeyi hesapla" }).click();
  await page.getByText(/aday sürüm/).waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  assert.ok(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  );
  await page.getByRole("button", { name: "Genel bakış", exact: true }).click();
  await page.screenshot({
    path: ".runtime/screenshots/mobile.png",
    fullPage: true,
  });
  assert.deepEqual(errors, []);
  console.log(
    "Browser: login, versions, integrity, retention preview and responsive layout passed.",
  );
} finally {
  await browser.close();
}
