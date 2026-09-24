import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { parseDataQuery } from '../public/query'
import ActiveFilterSummary from './ActiveFilterSummary'

function render(query: string, targetAnalytics = true) {
  const params = new URLSearchParams(query)
  return renderToStaticMarkup(createElement(ActiveFilterSummary, { route: { section: 'analytics', query: params }, filters: parseDataQuery(params).value, targetAnalytics }))
}

describe('active filter summary', () => {
  it('keeps selected region and source visible while dictionaries load', () => {
    const html = render('regionId=27&sourceIds=3&from=2026-09-24T08%3A00%3A00Z&to=2026-09-24T09%3A00%3A00Z')
    expect(html).toContain('Область: область №27')
    expect(html).toContain('Джерела: джерело №3')
    expect(html).toContain('Активні фільтри')
  })

  it('does not advertise target-only predicates for alert analytics', () => {
    const html = render('eventKinds=launch&categoryIds=1&regionId=27', false)
    expect(html).not.toContain('launch')
    expect(html).not.toContain('Категорії №1')
    expect(html).toContain('Область')
  })
})
