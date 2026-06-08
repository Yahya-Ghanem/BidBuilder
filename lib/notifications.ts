import type { NotificationItem } from "@/lib/types"

/**
 * 27.4 — A collapsed cluster of notifications that share an entity. Five "publish"
 * events on the same project become ONE row with a count, instead of five near-identical
 * lines. The newest item supplies the title/body/link/time for the collapsed row.
 */
export interface NotificationGroup {
  /** Stable React key for the group. */
  key: string
  /** Members, newest-first (same order as the source list). */
  items: NotificationItem[]
  /** The newest member — drives the collapsed row's title/body/link/time. */
  latest: NotificationItem
  /** Total members in the group. */
  count: number
  /** How many members are still unread. */
  unreadCount: number
}

/**
 * Group a newest-first notification list by (type + entity), preserving order.
 *
 * Items WITHOUT an entityKey are never collapsed together — each becomes its own
 * singleton group keyed by its id — so unrelated events (e.g. two different mentions
 * with no entity) don't merge just because they share a type. Items WITH an entityKey
 * collapse by `type|entityType|entityKey`, which is exactly the de-dup key the backend
 * stamps on notifications.
 *
 * Input is assumed newest-first (the API orders by id descending); the first member of
 * each group is therefore its newest, and group order follows first appearance.
 */
export function groupNotifications(items: NotificationItem[]): NotificationGroup[] {
  const byKey = new Map<string, NotificationItem[]>()
  const order: string[] = []
  for (const n of items) {
    const key = n.entityKey ? `e|${n.type}|${n.entityType ?? ""}|${n.entityKey}` : `s|${n.id}`
    let bucket = byKey.get(key)
    if (!bucket) { bucket = []; byKey.set(key, bucket); order.push(key) }
    bucket.push(n)
  }
  return order.map((key) => {
    const groupItems = byKey.get(key)!
    return {
      key,
      items: groupItems,
      latest: groupItems[0],
      count: groupItems.length,
      unreadCount: groupItems.reduce((acc, x) => acc + (x.isRead ? 0 : 1), 0),
    }
  })
}

/** Split grouped notifications into the bell's two sections: any group with at least one
 * unread member is "unread"; fully-read groups are "earlier". Order is preserved. */
export function partitionGroups(groups: NotificationGroup[]): { unread: NotificationGroup[]; earlier: NotificationGroup[] } {
  const unread: NotificationGroup[] = []
  const earlier: NotificationGroup[] = []
  for (const g of groups) (g.unreadCount > 0 ? unread : earlier).push(g)
  return { unread, earlier }
}
