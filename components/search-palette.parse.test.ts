import { describe, it, expect } from "vitest"
import { parseQuery } from "@/components/search-palette"

/**
 * 28.5 — parseQuery unit tests.
 *
 * The palette has three lanes (recent / actions / search-with-optional-@filter)
 * and the parser is the gatekeeper. Bugs here would either trap a user in
 * action mode forever, or drop them into the search lane when they typed
 * `>` first and expected actions. Cover each branch + the edge cases.
 */
describe("parseQuery", () => {
  it("empty input → recent mode", () => {
    expect(parseQuery("")).toEqual({ mode: "recent", term: "" })
    expect(parseQuery("   ")).toEqual({ mode: "recent", term: "" })
  })

  it("`>` prefix → action mode, term is the trimmed remainder", () => {
    expect(parseQuery(">")).toEqual({ mode: "actions", term: "" })
    expect(parseQuery("> ")).toEqual({ mode: "actions", term: "" })
    expect(parseQuery("> new project")).toEqual({ mode: "actions", term: "new project" })
    expect(parseQuery(">sign out")).toEqual({ mode: "actions", term: "sign out" })
  })

  it("`@p` → search mode + project filter; rest of the input is the term", () => {
    expect(parseQuery("@p")).toEqual({ mode: "search", entityType: "project", term: "" })
    expect(parseQuery("@p Sun")).toEqual({ mode: "search", entityType: "project", term: "Sun" })
    expect(parseQuery("@proj Sunrise")).toEqual({ mode: "search", entityType: "project", term: "Sunrise" })
    // Case insensitive: `@P` still means project.
    expect(parseQuery("@P alpha")).toEqual({ mode: "search", entityType: "project", term: "alpha" })
  })

  it("other known prefixes route to the right entity type", () => {
    // `.entityType` only exists on the search branch of the union, so each
    // assertion uses toEqual on the whole object — this also catches a future
    // bug where the parser leaks the prefix into the term.
    expect(parseQuery("@e Bid")).toEqual({ mode: "search", entityType: "estimate", term: "Bid" })
    expect(parseQuery("@r cement")).toEqual({ mode: "search", entityType: "resource", term: "cement" })
    expect(parseQuery("@a wall")).toEqual({ mode: "search", entityType: "assembly", term: "wall" })
    expect(parseQuery("@asm wall")).toEqual({ mode: "search", entityType: "assembly", term: "wall" })
  })

  it("unknown @ prefix → search mode, no filter, term is the whole string", () => {
    // `@x foo` is not a known tag — fall through to search so the user
    // gets matches instead of an empty pane.
    expect(parseQuery("@x foo")).toEqual({ mode: "search", entityType: null, term: "@x foo" })
    expect(parseQuery("@xyz")).toEqual({ mode: "search", entityType: null, term: "@xyz" })
  })

  it("plain text → search mode, term verbatim", () => {
    expect(parseQuery("Sunrise")).toEqual({ mode: "search", entityType: null, term: "Sunrise" })
    expect(parseQuery("  Sunrise  ")).toEqual({ mode: "search", entityType: null, term: "Sunrise" })
  })

  it("leading whitespace before a sigil is fine", () => {
    expect(parseQuery("  > sign")).toEqual({ mode: "actions", term: "sign" })
    expect(parseQuery("  @p Sun")).toEqual({ mode: "search", entityType: "project", term: "Sun" })
  })
})
