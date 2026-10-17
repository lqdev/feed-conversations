import { defineConfig } from "@playwright/test";
import development from "./playwright.config";

export default defineConfig({
  ...development,
  testMatch: "standalone.spec.ts",
  timeout: 90_000,
});
