import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import DataFilterControls from './DataFilterControls'
import type { PublicRoute } from '../public/routes'

function render(section: PublicRoute['section'], query = '') {
  return renderToStaticMarkup(createElement(DataFilterControls, { route: { section, query: new URLSearchParams(query) } }))
}

describe('section-specific public filter controls', () => {
  it('keeps retired URL selection visible with an explicit clearable checkbox', () => {
    const html = render('map', 'entityKinds=track&sourceIds=999')
    expect(html).toContain('Треки вимкнено')
    expect(html).toContain('Збережений фільтр треків')
    expect(html).toContain('Недоступне джерело #999')
    expect(html).toContain('type="checkbox"')
    expect(html).not.toContain('<select multiple')
    expect(html).toContain('Скинути фільтри даних')
  })
  it.each(['map', 'entities'] as const)('exposes only supported EE controls for %s', (section) => {
    const html = render(section)
    for (const label of ['Пошук', 'Тип сутності', 'Джерела', 'Період']) expect(html).toContain(label)
    for (const label of ['Вид події', 'Категорія', 'Клас', 'Сімейство', 'Модель', 'Область', 'Статус', 'Впевненість', 'Локація']) expect(html).not.toContain(label)
  })

  it('retains legacy analytics predicates without exposing unsupported search or EE kinds', () => {
    const html = render('analytics', 'metric=targets')
    for (const label of ['Вид події', 'Категорія', 'Клас', 'Сімейство', 'Модель', 'Область', 'Джерела', 'Період']) expect(html).toContain(label)
    expect(html).not.toContain('Пошук')
    expect(html).not.toContain('Тип сутності')
  })

  it('limits alert analytics to source, region and time without misleading active chips', () => {
    const html = render('analytics', 'metric=alerts&entityKinds=target&q=needle&categoryIds=1')
    for (const label of ['Область', 'Джерела', 'Період']) expect(html).toContain(label)
    for (const label of ['Пошук', 'Тип сутності', 'Категорія', 'Вид події', 'needle']) expect(html).not.toContain(label)
  })
})
