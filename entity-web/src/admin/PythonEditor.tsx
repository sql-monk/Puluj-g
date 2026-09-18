import { basicSetup } from 'codemirror'
import { python } from '@codemirror/lang-python'
import { forceLinting, linter, type Diagnostic } from '@codemirror/lint'
import { EditorState } from '@codemirror/state'
import { EditorView, keymap } from '@codemirror/view'
import { searchKeymap } from '@codemirror/search'
import { useEffect, useRef, useState } from 'react'

export interface PythonDiagnostic { line: number; column?: number; endLine?: number; endColumn?: number; message: string; severity?: 'error' | 'warning' | 'info' }

export default function PythonEditor({ value, onChange, diagnostics = [] }: { value: string; onChange: (value: string) => void; diagnostics?: PythonDiagnostic[] }) {
  const host = useRef<HTMLDivElement>(null)
  const view = useRef<EditorView | undefined>(undefined)
  const change = useRef(onChange)
  const [cursor, setCursor] = useState('1:1')
  const diagnosticRef = useRef(diagnostics)
  const [initialValue] = useState(value)
  useEffect(() => { change.current = onChange }, [onChange])
  useEffect(() => { diagnosticRef.current = diagnostics; if (view.current) forceLinting(view.current) }, [diagnostics])

  useEffect(() => {
    if (!host.current) return
    const editor = new EditorView({
      parent: host.current,
      state: EditorState.create({ doc: initialValue, extensions: [basicSetup, python(), keymap.of(searchKeymap), EditorView.lineWrapping,
        EditorView.updateListener.of((update) => {
          if (update.docChanged) change.current(update.state.doc.toString())
          if (update.selectionSet || update.docChanged) { const pos = update.state.doc.lineAt(update.state.selection.main.head); setCursor(`${pos.number}:${update.state.selection.main.head - pos.from + 1}`) }
        }),
        linter((current) => diagnosticRef.current.map((item): Diagnostic => {
          const line = current.state.doc.line(Math.min(Math.max(item.line, 1), current.state.doc.lines))
          const from = Math.min(line.to, line.from + Math.max((item.column ?? 1) - 1, 0))
          return { from, to: Math.max(from + 1, line.to), message: item.message, severity: item.severity ?? 'error' }
        }), { delay: 0 }),
      ] }),
    })
    view.current = editor
    return () => { editor.destroy(); view.current = undefined }
  }, [initialValue])

  useEffect(() => {
    const editor = view.current
    if (!editor || editor.state.doc.toString() === value) return
    editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: value } })
  }, [value])

  return <div className="overflow-hidden rounded border border-slate-300 bg-white dark:border-slate-600 dark:bg-slate-900"><div ref={host} className="min-h-80 text-sm" /><div className="border-t border-slate-200 px-2 py-1 text-right text-xs text-slate-500 dark:border-slate-700">Рядок:стовпець {cursor}</div></div>
}
