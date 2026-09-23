import { useState } from 'react'
import { admin, type AdminMessageDto, type AdminSourceDto } from '../api/admin'
import { Badge, Section } from '../components/settings/fields'
import { telegramTitle } from '../components/settings/sources'
import { messageFilterFromQuery, messagesHash, PROCESSING_STATUS } from './messages'
import { Loading, PagerButtons, fmtTime, usePaged } from './shared'

/**
 * Collected messages of one source, newest received first, with their processing state — where "Дивитись повідомлення" of
 * the collectors and sources leads. `#/messages?sourceId=86` or `#/messages?rawMessageId=123`.
 */
export function MessagesPanel({ query, sources }: { query: string; sources: AdminSourceDto[] }) {
  const filter = messageFilterFromQuery(query)
  const page = usePaged<AdminMessageDto, string>(async (cursor) => {
    if (filter.sourceId === undefined && filter.rawMessageId === undefined) return { items: [] }
    const r = await admin.ops.messages(filter, cursor)
    return { items: r.messages, next: r.nextCursor }
  }, [filter.sourceId, filter.rawMessageId])
  const source = sources.find((s) => s.id === filter.sourceId)
  const sorted = [...sources].sort((a, b) => telegramTitle(a.name, a.channelTitle).localeCompare(telegramTitle(b.name, b.channelTitle), 'uk'))
  const title = filter.rawMessageId !== undefined ? `Повідомлення #${filter.rawMessageId}` : source ? `Повідомлення: ${telegramTitle(source.name, source.channelTitle)}` : 'Повідомлення'
  return (
    <Section title={title} badge={page.items && <Badge ok={null} text={`${page.items.length.toLocaleString('uk-UA')}${page.hasMore ? '+' : ''}`} />}>
      <div className="flex flex-wrap items-end gap-2 text-xs">
        <label>
          <span className="mb-0.5 block text-slate-500">Джерело</span>
          <select className="rounded border border-slate-300 bg-white px-2 py-1 dark:border-slate-600 dark:bg-slate-800" aria-label="Джерело повідомлень" value={filter.sourceId ?? ''} onChange={(e) => window.location.assign(messagesHash({ sourceId: e.target.value ? Number(e.target.value) : undefined }))}>
            <option value="">— виберіть джерело —</option>
            {sorted.map((s) => (
              <option key={s.id} value={s.id}>
                {telegramTitle(s.name, s.channelTitle)} ({s.code})
              </option>
            ))}
          </select>
        </label>
        <MessageIdSearch key={filter.rawMessageId ?? 'none'} initial={filter.rawMessageId} />
      </div>
      {filter.sourceId === undefined && filter.rawMessageId === undefined ? (
        <p className="text-xs text-slate-500">Виберіть джерело або введіть ID повідомлення.</p>
      ) : (
        <>
          <Loading error={page.error} empty={page.items === null} />
          {page.items && page.items.length === 0 && <p className="text-xs text-slate-500">{filter.rawMessageId !== undefined ? 'Повідомлення з таким ID немає.' : 'У цього джерела ще немає повідомлень.'}</p>}
          {page.items && page.items.length > 0 && <MessagesTable messages={page.items} showSource={filter.sourceId === undefined} />}
          <PagerButtons busy={page.busy} hasMore={page.hasMore} count={page.items?.length} onMore={page.more} onRefresh={page.refresh} />
        </>
      )}
    </Section>
  )
}

function MessageIdSearch({ initial }: { initial?: number }) {
  const [value, setValue] = useState(initial === undefined ? '' : String(initial))
  const valid = /^\d+$/.test(value.trim())
  return (
    <form
      className="flex items-end gap-1"
      onSubmit={(e) => {
        e.preventDefault()
        if (valid) window.location.assign(messagesHash({ rawMessageId: Number(value.trim()) }))
      }}
    >
      <label>
        <span className="mb-0.5 block text-slate-500">або ID повідомлення</span>
        <input className="w-32 rounded border border-slate-300 px-2 py-1 font-mono dark:border-slate-600 dark:bg-slate-800" inputMode="numeric" aria-label="ID повідомлення" value={value} onChange={(e) => setValue(e.target.value)} />
      </label>
      <button className="rounded border border-slate-300 px-2 py-1 disabled:opacity-50 dark:border-slate-600" disabled={!valid}>
        Відкрити
      </button>
    </form>
  )
}

function MessagesTable({ messages, showSource }: { messages: AdminMessageDto[]; showSource: boolean }) {
  const [open, setOpen] = useState<number | null>(null)
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-xs">
        <thead className="text-left text-slate-500">
          <tr>
            <th className="py-1 pr-2">ID</th>
            {showSource && <th className="pr-2">Джерело</th>}
            <th className="pr-2">Опубліковано</th>
            <th className="pr-2">Отримано</th>
            <th className="pr-2">Обробка</th>
            <th className="pr-2">Цілей</th>
            <th className="pr-2">Текст</th>
          </tr>
        </thead>
        <tbody>
          {messages.map((m) => {
            const status = PROCESSING_STATUS[m.processingStatus] ?? { text: m.processingStatus, ok: null }
            const expanded = open === m.id
            return (
              <tr key={m.id} className="border-t border-slate-100 align-top dark:border-slate-800" data-testid="admin-message">
                <td className="whitespace-nowrap py-1.5 pr-2 font-mono">
                  {m.url ? (
                    <a className="underline" href={m.url} target="_blank" rel="noreferrer">
                      #{m.id}
                    </a>
                  ) : (
                    `#${m.id}`
                  )}
                </td>
                {showSource && <td className="pr-2 font-mono">{m.sourceCode}</td>}
                <td className="whitespace-nowrap pr-2" title={m.publishedAt}>
                  {fmtTime(m.publishedAt)}
                </td>
                <td className="whitespace-nowrap pr-2 text-slate-500" title={m.receivedAt}>
                  {fmtTime(m.receivedAt)}
                </td>
                <td className="whitespace-nowrap pr-2" title={m.processedAt ? `оброблено ${fmtTime(m.processedAt)}, спроб ${m.attempts}` : `спроб ${m.attempts}`}>
                  <Badge ok={status.ok} text={status.text} />
                </td>
                <td className="pr-2 font-mono">{m.targets}</td>
                <td className="pr-2">
                  <button className={`block max-w-xl text-left ${expanded ? 'whitespace-pre-wrap break-words' : 'truncate'}`} title={expanded ? 'Згорнути' : 'Показати повністю'} aria-expanded={expanded} onClick={() => setOpen(expanded ? null : m.id)}>
                    {m.text ?? <span className="text-slate-400">без тексту</span>}
                    {expanded && m.textTruncated && <span className="text-slate-400"> … (обрізано до 2000 символів)</span>}
                  </button>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
