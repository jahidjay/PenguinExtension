// @vitest-environment node
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import type { Job, Status, SymbolDto } from "../src/types";
function fixture(name: string) {
  return JSON.parse(
    readFileSync(
      resolve(process.cwd(), "../../docs/fixtures", name + ".json"),
      "utf8",
    ),
  );
}
it("matches shared status, symbol and preview-job fixtures", () => {
  const status: Status = fixture("status");
  const symbol: SymbolDto = fixture("symbol");
  const job: Job = fixture("job");
  expect(status.protocolVersion).toBe(1);
  expect(status.roots).toContain("/workspace/Game");
  expect(symbol.macroName).toBe("UCLASS");
  expect(symbol.specifiers[0].key).toBe("Blueprintable");
  expect(symbol.signature).toContain("AExampleActor");
  expect(job.result?.text).toContain("Preview only");
  expect(job.state).toBe("succeeded");
});

it("native package scripts embed assets and never require a Vite server", () => {
  const pkg = JSON.parse(
    readFileSync(resolve(process.cwd(), "package.json"), "utf8"),
  );
  expect(pkg.scripts["desktop:build"]).toContain("--features custom-protocol");
  expect(pkg.scripts["desktop:portable"]).toContain(
    "--features custom-protocol",
  );
});
