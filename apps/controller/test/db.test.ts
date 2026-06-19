import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

import { AppDb } from "../src/db.js";

describe("AppDb active game", () => {
  it("stores, reads, and clears current game details", () => {
    const dir = mkdtempSync(join(tmpdir(), "d2r-db-"));
    const db = new AppDb(join(dir, "controller.sqlite"));

    db.setActiveGame({
      name: "baal-001",
      password: "pw",
      difficulty: "hell",
      notes: "join off host",
      updatedBy: "123"
    });

    expect(db.getActiveGame()).toMatchObject({
      name: "baal-001",
      password: "pw",
      difficulty: "hell",
      notes: "join off host",
      updatedBy: "123"
    });

    expect(db.clearActiveGame()).toBe(true);
    expect(db.getActiveGame()).toBeUndefined();
  });
});
