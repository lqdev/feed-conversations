import { defineConfig } from "@playwright/test";
import { readFileSync } from "node:fs";
import { X509Certificate, createHash } from "node:crypto";
import { resolve } from "node:path";

const certificate = new X509Certificate(readFileSync(resolve(".runtime/localhost.crt")));
const spki = createHash("sha256").update(certificate.publicKey.export({ type: "spki", format: "der" })).digest("base64");
export default defineConfig({
  testDir: ".",
  testMatch: "*.spec.ts",
  timeout: 180_000,
  expect: { timeout: 20_000 },
  workers: 1,
  retries: 0,
  reporter: "list",
  outputDir: "../../.runtime/browser-results",
  use: {
    baseURL: "https://localhost:7440",
    trace: "off",
    screenshot: "off",
    launchOptions: { args: [`--ignore-certificate-errors-spki-list=${spki}`] },
  },
});
