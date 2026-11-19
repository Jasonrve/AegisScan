import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'

const WATCH_KEY = 'aegis-watch'
const MODE_KEY = 'aegis-scope'
// shortcut: the watch list lives in this browser, not on the server per user; move it to a per-user table when people share browsers or want it on every device.
const readWatch = (): string[] => { try { const v = JSON.parse(localStorage.getItem(WATCH_KEY) || '[]'); return Array.isArray(v) ? v.filter(x => typeof x === 'string') : [] } catch { return [] } }
const readMode = (): 'mine' | 'all' => { try { return localStorage.getItem(MODE_KEY) === 'mine' ? 'mine' : 'all' } catch { return 'all' } }
const write = (key: string, value: string) => { try { localStorage.setItem(key, value) } catch { /* private window or blocked storage: the choice just lasts for this page */ } }

export type Scope = {
  watched: Set<string>
  mode: 'mine' | 'all'
  /** Repository ids every view should be limited to; null = all (also when "mine" is chosen but nothing is starred yet). */
  ids: string[] | null
  has: (id: string) => boolean
  toggle: (id: string) => void
  setMode: (mode: 'mine' | 'all') => void
}
const ScopeContext = createContext<Scope>({ watched: new Set(), mode: 'all', ids: null, has: () => false, toggle: () => {}, setMode: () => {} })
export const useScope = () => useContext(ScopeContext)

export function ScopeProvider({ children }: { children: ReactNode }) {
  const [watched, setWatched] = useState(() => new Set(readWatch()))
  const [mode, setModeState] = useState(readMode)
  const toggle = useCallback((id: string) => setWatched(current => { const next = new Set(current); next.has(id) ? next.delete(id) : next.add(id); write(WATCH_KEY, JSON.stringify([...next])); return next }), [])
  const setMode = useCallback((m: 'mine' | 'all') => { setModeState(m); write(MODE_KEY, m) }, [])
  const value = useMemo<Scope>(() => ({ watched, mode, ids: mode === 'mine' && watched.size ? [...watched].sort() : null, has: id => watched.has(id), toggle, setMode }), [watched, mode, toggle, setMode])
  return <ScopeContext.Provider value={value}>{children}</ScopeContext.Provider>
}

/** "My repositories / All" switch shown in the heading of every view that honours the scope. */
export function ScopeSwitch() {
  const s = useScope()
  return <div className="scope-switch" role="group" aria-label="Scope">
    <button aria-pressed={s.mode === 'mine'} onClick={() => s.setMode('mine')}>My repositories ({s.watched.size})</button>
    <button aria-pressed={s.mode === 'all'} onClick={() => s.setMode('all')}>All</button>
    {s.mode === 'mine' && s.watched.size === 0 && <small>Star the repositories you own to build your list; until then everything is shown.</small>}
  </div>
}

export function StarButton({ id, name }: { id: string; name: string }) {
  const s = useScope(); const on = s.has(id)
  return <button type="button" className={`star ${on ? 'on' : ''}`} aria-pressed={on} aria-label={`${on ? 'Stop watching' : 'Watch'} ${name}`} title={on ? 'In my repositories' : 'Add to my repositories'} onClick={e => { e.stopPropagation(); s.toggle(id) }}>{on ? '★' : '☆'}</button>
}
