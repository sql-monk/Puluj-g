// Pure helpers of the message browser: its hash route and the wording of processing states.

export interface MessageFilter {
  sourceId?: number
  rawMessageId?: number
}

function positiveInt(value: string | null): number | undefined {
  if (!value || !/^\d+$/.test(value.trim())) return undefined
  const n = Number(value.trim())
  return n > 0 ? n : undefined
}

/** `sourceId=86` or `rawMessageId=123`; the older `sourceIds=86,87` link form opens its first source. */
export function messageFilterFromQuery(query: string): MessageFilter {
  const params = new URLSearchParams(query.replace(/^\?/, ''))
  const rawMessageId = positiveInt(params.get('rawMessageId'))
  if (rawMessageId !== undefined) return { rawMessageId }
  const sourceId = positiveInt(params.get('sourceId')) ?? positiveInt(params.get('sourceIds')?.split(',')[0] ?? null)
  return sourceId === undefined ? {} : { sourceId }
}

export function messagesHash(filter: MessageFilter): string {
  if (filter.rawMessageId !== undefined) return `#/messages?rawMessageId=${filter.rawMessageId}`
  if (filter.sourceId !== undefined) return `#/messages?sourceId=${filter.sourceId}`
  return '#/messages'
}

export const PROCESSING_STATUS: Record<string, { text: string; ok: boolean | null }> = {
  Pending: { text: 'очікує', ok: null },
  InProgress: { text: 'обробляється', ok: null },
  Processed: { text: 'оброблено', ok: true },
  Skipped: { text: 'пропущено', ok: null },
  Failed: { text: 'помилка', ok: false },
}

/**
 * How a row relates to its Telegram post: a stored edit is its own row whose "published" time is the edit time, and the
 * original post says that later edits exist — otherwise two rows with the same link look like one post stored twice.
 */
export function revisionNote(m: { sourceMessageKey: string; sourceRevision: string; revisions: number }): { label: string; title: string } | null {
  if (m.sourceRevision && m.sourceRevision !== '0') {
    return {
      label: 'редакція',
      title: `Редагування поста ${m.sourceMessageKey}: «Опубліковано» — час редагування, не першої публікації. Версій цього поста в базі: ${m.revisions}.`,
    }
  }
  if (m.revisions > 1) {
    return { label: `версій: ${m.revisions}`, title: `Оригінал поста ${m.sourceMessageKey}; його редагування збережені окремими рядками з тим самим посиланням.` }
  }
  return null
}
