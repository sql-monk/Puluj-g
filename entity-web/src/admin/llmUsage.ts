import { fmtNum } from './format'

// Pure wording of the LLM usage cards: every part of a total is named, so the parts add up to the number shown.

interface Usage {
  calls: number
  withFacts: number
  empty: number
  refusals: number
  failures: number
  inputTokens: number
  cacheWriteTokens: number
  cacheReadTokens: number
}

/** facts + empty + refusals + successes (EE / plain successful answers) + failures = calls. */
export function callsHint(u: Usage): string {
  const successes = Math.max(0, u.calls - u.withFacts - u.empty - u.refusals - u.failures)
  return `фактів ${fmtNum(u.withFacts)} · порожньо ${fmtNum(u.empty)} · відмов ${fmtNum(u.refusals)} · успішні (EE) ${fmtNum(successes)} · помилок ${fmtNum(u.failures)}`
}

/** The input card sums all three kinds of input tokens; the hint names each. */
export function inputTokens(u: Usage): { total: number; hint: string } {
  return {
    total: u.inputTokens + u.cacheWriteTokens + u.cacheReadTokens,
    hint: `звичайні ${fmtNum(u.inputTokens)} · cache write ${fmtNum(u.cacheWriteTokens)} · cache read ${fmtNum(u.cacheReadTokens)}`,
  }
}
