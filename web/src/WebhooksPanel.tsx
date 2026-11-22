import { useEffect, useState, type ChangeEvent, type FormEvent } from 'react'
import { loadWebhooks, saveWebhook, deleteWebhook, type Webhook } from './dataClient'

const blank = { name: '', url: '', secret: '', repositoryId: '', scanner: '' }

/** Admin-only: endpoints that receive the SARIF of a repository's findings (event "sarif.updated") after each successful scan. */
export default function WebhooksPanel({ token, repos, scanners }: { token: string; repos: { id: string; name: string }[]; scanners: { id: string; name: string }[] }) {
  const [hooks, setHooks] = useState<Webhook[] | null>(null)
  const [form, setForm] = useState(blank)
  const [message, setMessage] = useState('')
  const reload = () => loadWebhooks(token).then(setHooks).catch(() => { setHooks([]); setMessage('Could not load webhooks.') })
  useEffect(() => { reload() }, [token])
  const run = async (action: () => Promise<unknown>, done = '') => { try { await action(); setMessage(done); await reload() } catch (e) { setMessage(`Failed: ${(e as Error).message}`) } }
  const add = (e: FormEvent) => { e.preventDefault(); run(async () => { await saveWebhook({ ...form, id: crypto.randomUUID() }, token); setForm(blank) }, 'Webhook added.') }
  const set = (e: ChangeEvent<HTMLInputElement | HTMLSelectElement>) => setForm({ ...form, [e.target.name]: e.target.value })
  return <section className="panel webhooks-panel">
    <div className="panel-head"><div><h2>SARIF webhooks</h2><p className="feature-note">After every successful scan, POST the SARIF of the repository's findings for that scanner (<code>sarif.updated</code>) to each matching endpoint. Signed with <code>X-Aegis-Signature</code> when a secret is set. Pull on demand instead with <code>GET /api/v1/repositories/&#123;id&#125;/sarif?scanner=&amp;executionId=&amp;state=</code>.</p></div></div>
    {hooks?.length ? <div className="wh-list">{hooks.map(h => <div className="wh-row" key={h.id}>
      <div><b>{h.name}</b><small>{h.url}</small></div>
      <small>{h.repositoryId || 'all repositories'} · {h.scanner || 'all scanners'}{h.hasSecret ? ' · signed' : ''}</small>
      <button className="secondary" onClick={() => run(() => saveWebhook({ id: h.id, name: h.name, url: h.url, repositoryId: h.repositoryId, scanner: h.scanner, enabled: !h.enabled }, token))}>{h.enabled ? 'Disable' : 'Enable'}</button>
      <button className="secondary" onClick={() => run(() => deleteWebhook(h.id, token), 'Webhook deleted.')}>Delete</button>
    </div>)}</div> : <div className="empty-state"><span>{hooks ? 'No webhooks configured.' : 'Loading…'}</span></div>}
    <form className="wh-form" onSubmit={add}>
      <input name="name" aria-label="Webhook name" placeholder="Name" value={form.name} onChange={set}/>
      <input name="url" aria-label="Webhook URL" placeholder="https://example.com/hooks/aegis" required value={form.url} onChange={set}/>
      <input name="secret" aria-label="Webhook secret" type="password" placeholder="Signing secret (optional)" value={form.secret} onChange={set}/>
      <select name="repositoryId" aria-label="Repository" value={form.repositoryId} onChange={set}><option value="">All repositories</option>{repos.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}</select>
      <select name="scanner" aria-label="Scanner" value={form.scanner} onChange={set}><option value="">All scanners</option>{scanners.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
      <button className="primary" type="submit">Add webhook</button>
      <small aria-live="polite">{message}</small>
    </form>
  </section>
}
