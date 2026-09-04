import { describe, expect, it } from 'vitest'
import { remember } from './dedupe'

describe('remember', () => {
  it('accepts the first sighting of an ID', () => {
    expect(remember('a', new Set(), [], 10)).toBe(true)
  })

  it('rejects replays of a remembered ID', () => {
    const seen = new Set<string>()
    const order: string[] = []
    expect(remember('a', seen, order, 10)).toBe(true)
    expect(remember('a', seen, order, 10)).toBe(false)
  })

  it('rejects empty IDs', () => {
    expect(remember('', new Set(), [], 10)).toBe(false)
  })

  it('evicts the oldest ID once over capacity, keeping memory bounded', () => {
    const seen = new Set<string>()
    const order: string[] = []
    expect(remember('a', seen, order, 2)).toBe(true)
    expect(remember('b', seen, order, 2)).toBe(true)
    expect(remember('c', seen, order, 2)).toBe(true)
    expect(seen.size).toBe(2)
    // The evicted ID reads as new again: the window is bounded, not a ledger.
    expect(remember('a', seen, order, 2)).toBe(true)
  })
})
