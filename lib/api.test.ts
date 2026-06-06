import { describe, it, expect } from "vitest"
import { isAuthUser } from "./api"

describe("isAuthUser", () => {
  it("accepts a well-formed user", () => {
    expect(isAuthUser({ id: 1, name: "A", email: "a@b.c", role: "TenantAdmin" })).toBe(true)
  })

  it.each([
    null,
    undefined,
    {},
    "a string",
    { id: "1", name: "A", email: "a@b.c", role: "x" }, // id not a number
    { id: 1, name: "A", email: "a@b.c" },              // missing role
    { id: 1, name: 5, email: "a@b.c", role: "x" },     // name not a string
  ])("rejects a malformed payload (%o)", (bad) => {
    expect(isAuthUser(bad)).toBe(false)
  })
})
