import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { MapId, TargetDto, TrackDetailsDto } from '../api/types'
import { useEta } from '../eta/useEta'
import { clock, confidenceLabel, dateTime, directionText, etaText, fixChain, locationKindLabel } from '../lib/format'
import { usePalette, useStore } from '../store/useStore'
import Highlight from './Highlight'

interface Props {
  onClose: () => void
}

const eventLabel: Record<string, string> = {
  TargetObserved: 'спостереження',
  AirRaidAlert: 'тривога',
  AlertCancelled: 'відбій тривоги',
  TargetCancelled: 'загроза минула',
  ExplosionReport: 'вибухи',
  AirDefenseActivity: 'робота ППО',
}

const linkLabel: Record<string, string> = { Continuation: 'продовження', Split: 'розділення', Merge: 'злиття', Possible: 'можливий зв’язок', Duplicate: 'дубль' }

const methodLabel: Record<string, string> = { Rule: 'правила', Llm: 'LLM', Structured: 'структуроване джерело', Manual: 'вручну' }

/**
 * Spec §18 / §31: the full provenance chain of the selected track — every target, its source and the original
 * message. A left-hand panel that stays open and follows the selection: pick another target on the map and it
 * shows that one. Only one target is selected at a time.
 */
export default function TrackDetailsDrawer({ onClose }: Props) {
  const trackId = useStore((s) => s.selectedTrackId)
  const live = useStore((s) => (s.selectedTrackId ? s.tracks[s.selectedTrackId] : undefined))
  const sourceFilter = useStore((s) => s.filters.sources)
  const palette = usePalette()
  const [data, setData] = useState<TrackDetailsDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const eta = useEta(live ?? data?.track)
  const version = live?.targetCount ?? 0

  // Loaded for the selected track and refreshed when it gains targets (debounced; an older reply still lands).
  const seq = useRef(0)
  const applied = useRef(0)
  useEffect(() => {
    applied.current = 0
    setData(null)
    setError(null)
  }, [trackId])
  useEffect(() => {
    if (!trackId) return
    const id = ++seq.current
    const handle = window.setTimeout(() => {
      api
        .track(trackId)
        .then((d) => {
          if (id > applied.current) {
            applied.current = id
            setData(d)
            setError(null)
          }
        })
        .catch((e: Error) => {
          if (id > applied.current) setError(e.message)
        })
    }, applied.current === 0 ? 0 : 1500)
    return () => window.clearTimeout(handle)
  }, [trackId, version])

  const track = live ?? data?.track
  const targets = (data?.targets ?? []).filter((o) => sourceFilter === null || sourceFilter.includes(o.source.id)).slice().reverse()

  return (
    <div data-map-occlusion="true" className="pointer-events-auto absolute inset-y-0 left-0 top-12 z-30 flex w-full max-w-md flex-col bg-white shadow-2xl md:top-14 md:bottom-3 md:left-3 md:rounded-xl dark:bg-slate-900 dark:text-slate-100">
      <div className="flex items-center justify-between border-b border-slate-200 px-4 py-2.5 dark:border-slate-700">
        <div className="flex min-w-0 items-center gap-2 font-semibold">
          {track && <span className="inline-block h-3 w-3 shrink-0 rounded-full" style={{ background: palette.marker[track.type.displayMode] }} />}
          <span className="truncate">{track ? `${track.type.label} · трек #${track.id}` : 'Виділена ціль'}</span>
        </div>
        <button className="text-slate-400 hover:text-slate-700 dark:hover:text-slate-200" onClick={onClose} aria-label="Закрити">
          ✕
        </button>
      </div>
      <div className="flex-1 overflow-y-auto px-4 py-3 text-sm">
        {!trackId && <div className="text-slate-500">Клікніть по цілі на карті — тут з'являться її дані та всі повідомлення, з яких вона побудована. Панель слідує за виділенням.</div>}
        {error && <div className="text-red-600">{error}</div>}
        {trackId && !data && !error && <div className="text-slate-500">Завантаження…</div>}
        {track && (
          <section className="mb-4 rounded-lg bg-slate-50 p-3 text-xs dark:bg-slate-800">
            <div>Тип: {track.type.categoryName}{track.type.className ? ` / ${track.type.className}` : ''}</div>
            <div>Модель: {track.type.modelName ?? track.type.familyName ?? '—'} · впевненість: {confidenceLabel[track.modelConfidence]}</div>
            <div>Останнє повідомлення: {clock(track.lastSeenAt)}</div>
            <div>
              Район: {track.lastLocation?.placeName ?? '—'} <span className="text-slate-400">({locationKindLabel[track.lastLocation?.kind ?? 'Unknown']})</span>
            </div>
            <div>Напрямок: {directionText(track.direction)}</div>
            {track.fixes.length >= 2 && <div>Був: {fixChain(track)}</div>}
            <PredecessorList />
            <div>Впевненість треку: {confidenceLabel[track.trackConfidence]} · {track.targetCount} повід. · {Math.max(track.distinctSourceCount, track.sourceIds.length)} джерел</div>
            {track.objectCount && track.objectCount > 1 && <div>Цілей у групі: {track.objectCount}</div>}
            <div>ETA до вас: {etaText(eta)}</div>
            {track.status !== 'Active' && <div>Стан: {track.status === 'Cancelled' ? 'відбій' : 'закрито'}{track.closedReason ? ` (${track.closedReason})` : ''}</div>}
          </section>
        )}
        {data && (
          <>
            <div className="mb-1 text-[11px] font-medium text-slate-500">Повідомлення, з яких побудовано трек (новіші зверху)</div>
            <ol className="space-y-3">
              {targets.map((o) => (
                <TargetItem key={o.id} o={o} />
              ))}
              {targets.length === 0 && <li className="text-xs text-slate-500">Немає повідомлень від вибраних джерел.</li>}
            </ol>
          </>
        )}
      </div>
    </div>
  )
}

function TargetItem({ o }: { o: TargetDto }) {
  return (
    <li className={`rounded-lg border p-3 text-xs ${o.duplicateOfTargetId ? 'border-dashed border-slate-300 opacity-80 dark:border-slate-600' : 'border-slate-200 dark:border-slate-700'}`}>
      <div className="mb-1 flex flex-wrap items-baseline justify-between gap-x-2">
        <span className="font-mono text-sm">{clock(o.observedAt)}</span>
        <span className="font-medium">{o.source.name}</span>
        <span className="text-slate-400">довіра {Math.round(o.source.trustLevel * 100)}%</span>
      </div>
      <div className="mb-1 text-slate-600 dark:text-slate-300">
        {eventLabel[o.eventType] ?? o.eventType}
        {o.type && <> · {o.type.label} ({confidenceLabel[o.modelConfidence]})</>}
        {o.objectCount && <> · {o.objectCountIsApproximate ? '~' : ''}{o.objectCount} од.</>}
        {o.location?.placeName && <> · {o.location.placeName} <span className="text-slate-400">({locationKindLabel[o.location.kind]})</span></>}
        {o.destination && <> → {o.destination.placeName}</>}
        {o.direction && <> · {directionText(o.direction)}</>}
      </div>
      <div className="mb-1 text-[11px] text-slate-400">
        розпізнано: {methodLabel[o.identificationMethod] ?? o.identificationMethod}
        {o.identificationSource && <> («{o.identificationSource}»)</>} · впевненість спостереження {confidenceLabel[o.confidence]}
        {o.associationConfidence !== undefined && <> · зв'язок з треком {Math.round(o.associationConfidence * 100)}%</>}
        {o.duplicateOfTargetId && <> · дубль #{o.duplicateOfTargetId}</>}
      </div>
      {o.links && o.links.length > 0 && (
        <div className="mb-1 flex flex-wrap gap-1 text-[11px]">
          {o.links.map((l) => (
            <span
              key={`${l.direction}-${l.targetId}`}
              className="rounded bg-slate-100 px-1 text-slate-600 dark:bg-slate-800 dark:text-slate-300"
              style={{ opacity: 0.45 + 0.55 * l.probability }}
              title={`${linkLabel[l.kind] ?? l.kind}: ${Math.round(l.probability * 100)}%${l.distanceKm !== undefined ? ` · ${l.distanceKm} км за ${l.minutesApart} хв (треба ~${l.requiredMinutes})` : ''}${l.headingDiffDeg !== undefined ? ` · відхилення курсу ${l.headingDiffDeg}°` : ''}`}
            >
              {l.direction === 'from' ? '←' : '→'} #{l.targetId} {linkLabel[l.kind] ?? l.kind} {Math.round(l.probability * 100)}%
            </span>
          ))}
        </div>
      )}
      <blockquote className="whitespace-pre-wrap rounded bg-slate-50 p-2 text-[12px] leading-snug text-slate-800 dark:bg-slate-800 dark:text-slate-200">
        {o.rawMessage.text ? <Highlight text={o.rawMessage.text} part={o.segmentText ?? ''} /> : (o.segmentText ?? '(без тексту)')}
      </blockquote>
      <div className="mt-1 flex justify-between text-[11px] text-slate-400">
        <span>
          опубліковано {dateTime(o.rawMessage.publishedAt)} · отримано {clock(o.rawMessage.receivedAt)}
        </span>
        {o.rawMessage.url && (
          <a className="underline" href={o.rawMessage.url} target="_blank" rel="noreferrer">
            оригінал ↗
          </a>
        )}
      </div>
    </li>
  )
}

/** The probable predecessors of the selected track's newest report: the direct ones, then the ones behind each, and
 * under each of them where else it could have flown (its other successors). */
function PredecessorList() {
  const fork = useStore((s) => s.predecessors)
  if (!fork || fork.links.length === 0) return null
  const byId = new Map(fork.targets.map((n) => [n.targetId, n]))
  const time = (iso: string) => new Date(iso).toLocaleTimeString('uk-UA', { hour: '2-digit', minute: '2-digit' })
  const kindLabel: Record<string, string> = { Continuation: 'продовження', Split: 'розділення', Merge: 'злиття', Possible: 'можливо', Duplicate: 'дубль' }
  const gen1 = fork.links.filter((l) => l.ancestral && l.toTargetId === fork.headTargetId).sort((a, b) => b.probability - a.probability)
  const elsewhere = (from: MapId) => {
    const others = fork.links.filter((x) => !x.ancestral && x.fromTargetId === from).sort((a, b) => b.probability - a.probability)
    if (others.length === 0) return null
    return (
      <div className="ml-3 text-slate-500">
        також могло летіти:{' '}
        {others.map((s) => {
          const m = byId.get(s.toTargetId)
          return `${m?.approach ? '→ ' : ''}${m?.placeName ?? `#${s.toTargetId}`} ${m ? time(m.at) : ''} · ${Math.round(s.probability * 100)}%`
        }).join(', ')}
      </div>
    )
  }
  return (
    <div className="mt-1">
      <div>Ймовірні попередники (до 2 поколінь) і куди ще вони могли летіти:</div>
      <ul className="ml-2 space-y-0.5">
        {gen1.map((l) => {
          const n = byId.get(l.fromTargetId)
          const gen2 = fork.links.filter((x) => x.ancestral && x.toTargetId === l.fromTargetId).sort((a, b) => b.probability - a.probability)
          return (
            <li key={l.fromTargetId} style={{ opacity: 0.55 + 0.45 * l.probability }} title={`${kindLabel[l.kind] ?? l.kind} · ціль #${l.fromTargetId}${n?.label ? ` · «${n.label}»` : ''}`}>
              ← {n?.approach ? '→ ' : ''}{n?.placeName ?? `#${l.fromTargetId}`} {n ? time(n.at) : ''} · <b>{Math.round(l.probability * 100)}%</b>
              {elsewhere(l.fromTargetId)}
              {gen2.length > 0 && (
                <ul className="ml-3">
                  {gen2.map((g) => {
                    const m = byId.get(g.fromTargetId)
                    return (
                      <li key={g.fromTargetId} style={{ opacity: 0.55 + 0.45 * g.pathProbability }} title={`${kindLabel[g.kind] ?? g.kind} · ціль #${g.fromTargetId}${m?.label ? ` · «${m.label}»` : ''} · шлях ${Math.round(g.pathProbability * 100)}%`}>
                        ← {m?.approach ? '→ ' : ''}{m?.placeName ?? `#${g.fromTargetId}`} {m ? time(m.at) : ''} · {Math.round(g.probability * 100)}% <span className="text-slate-400">(шлях {Math.round(g.pathProbability * 100)}%)</span>
                        {elsewhere(g.fromTargetId)}
                      </li>
                    )
                  })}
                </ul>
              )}
            </li>
          )
        })}
      </ul>
    </div>
  )
}
