import { describe, expect, it } from "vitest";
import { createWindowOptions, isSecureWindowOptions } from "./window-options.js";

describe("Electron window security", () => {
  it("keeps Node out of the renderer and enables isolation", () => {
    const options = createWindowOptions("C:/qiongtu/preload.js");

    expect(isSecureWindowOptions(options)).toBe(true);
    expect(options.webPreferences?.preload).toBe("C:/qiongtu/preload.js");
  });
});
