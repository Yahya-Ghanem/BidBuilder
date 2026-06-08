import { describe, it, expect } from "vitest"
import { groupNotifications, partitionGroups } from "@/lib/notifications"
import type { NotificationItem } from "@/lib/types"

let seq = 100
const n = (over: Partial<NotificationItem> = {}): NotificationItem => ({
  id: seq--, type: "estimate.published", title: "Published", body: null, link: null,
  entityType: "Project", entityKey: null, isRead: false, createdAt: "2026-01-01T00:00:00Z", readAt: null,
  ...over,
})

describe("groupNotifications", () => {
  it("collapses items that share type + entity into one group with a count", () => {
    const items = [
      n({ id: 5, entityKey: "7", title: "A" }),
      n({ id: 4, entityKey: "7", title: "B" }),
      n({ id: 3, entityKey: "7", title: "C" }),
    ]
    const groups = groupNotifications(items)
    expect(groups).toHaveLength(1)
    expect(groups[0].count).toBe(3)
    expect(groups[0].latest.id).toBe(5)          // newest-first → first member is latest
    expect(groups[0].latest.title).toBe("A")
  })

  it("does NOT collapse different entities of the same type", () => {
    const groups = groupNotifications([
      n({ id: 5, entityKey: "7" }),
      n({ id: 4, entityKey: "8" }),
    ])
    expect(groups).toHaveLength(2)
  })

  it("never collapses items that have no entityKey (each is its own group)", () => {
    const groups = groupNotifications([
      n({ id: 5, entityKey: null }),
      n({ id: 4, entityKey: null }),
    ])
    expect(groups).toHaveLength(2)
    expect(groups.every((g) => g.count === 1)).toBe(true)
  })

  it("counts unread members per group", () => {
    const groups = groupNotifications([
      n({ id: 5, entityKey: "7", isRead: false }),
      n({ id: 4, entityKey: "7", isRead: true, readAt: "2026-01-02T00:00:00Z" }),
      n({ id: 3, entityKey: "7", isRead: false }),
    ])
    expect(groups[0].count).toBe(3)
    expect(groups[0].unreadCount).toBe(2)
  })

  it("preserves first-appearance (newest-first) order of groups", () => {
    const groups = groupNotifications([
      n({ id: 9, entityKey: "B" }),
      n({ id: 8, entityKey: "A" }),
      n({ id: 7, entityKey: "B" }),
    ])
    expect(groups.map((g) => g.key)).toEqual(["e|estimate.published|Project|B", "e|estimate.published|Project|A"])
  })
})

describe("partitionGroups", () => {
  it("puts any group with an unread member in 'unread', fully-read groups in 'earlier'", () => {
    const groups = groupNotifications([
      n({ id: 5, entityKey: "7", isRead: false }),                                   // unread group
      n({ id: 4, entityKey: "8", isRead: true, readAt: "2026-01-02T00:00:00Z" }),    // earlier group
    ])
    const { unread, earlier } = partitionGroups(groups)
    expect(unread).toHaveLength(1)
    expect(earlier).toHaveLength(1)
    expect(unread[0].latest.entityKey).toBe("7")
    expect(earlier[0].latest.entityKey).toBe("8")
  })

  it("a partially-read group stays in 'unread'", () => {
    const groups = groupNotifications([
      n({ id: 5, entityKey: "7", isRead: false }),
      n({ id: 4, entityKey: "7", isRead: true, readAt: "2026-01-02T00:00:00Z" }),
    ])
    const { unread, earlier } = partitionGroups(groups)
    expect(unread).toHaveLength(1)
    expect(earlier).toHaveLength(0)
  })
})
