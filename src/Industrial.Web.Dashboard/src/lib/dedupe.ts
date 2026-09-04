// Bounded identity window for the live UI. Kafka and the alert outbox are both
// at-least-once, so an ambiguous ACK can replay a message; remembering recent IDs
// keeps a replay from drawing the same point or alert twice.
export function remember(
    id: string,
    seen: Set<string>,
    order: string[],
    capacity: number,
): boolean {
    if (!id || seen.has(id)) return false

    seen.add(id)
    order.push(id)

    if (order.length > capacity) {
        const expired = order.shift()
        if (expired !== undefined) seen.delete(expired)
    }

    return true
}
