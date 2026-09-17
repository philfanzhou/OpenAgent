import { describe, expect, it } from 'vitest'
import { normalizeApprovalStatus } from './types'

describe('normalizeApprovalStatus', () => {
  it('maps numeric enum payloads to status text', () => {
    expect(normalizeApprovalStatus(0)).toBe('Pending')
    expect(normalizeApprovalStatus(1)).toBe('Approved')
    expect(normalizeApprovalStatus(2)).toBe('Rejected')
  })

  it('keeps string statuses and defaults missing values to Pending', () => {
    expect(normalizeApprovalStatus('Pending')).toBe('Pending')
    expect(normalizeApprovalStatus('Approved')).toBe('Approved')
    expect(normalizeApprovalStatus(undefined)).toBe('Pending')
    expect(normalizeApprovalStatus(null)).toBe('Pending')
    expect(normalizeApprovalStatus('')).toBe('Pending')
  })
})
