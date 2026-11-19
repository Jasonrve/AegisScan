import { useContext, useState } from 'react'
import { NavigationContext } from './PaginatedViews'
import type { AlertAck } from './dataClient'

export type Alert = { key: string; title: string; detail: string; severity: string; time?: string; target?: { kind: 'repo' | 'run' | 'findings'; id: string }; view?: string }

/** Alerts are derived from live signals; acknowledging or snoozing one is stored server-side (and audited), so it holds for every user and device. */
export default function AlertsView({ alerts, acks, canAck, canConfigure, onAck, onUnack, onNavigate, pretty }: {
  alerts: Alert[]; acks: AlertAck[]; canAck: boolean; canConfigure: boolean
  onAck: (key: string, snoozeHours?: number) => Promise<void>; onUnack: (key: string) => Promise<void>
  onNavigate: (view: string) => void; pretty: (value: any) => string
}) {
  const [shown, setShown] = useState(25)
  const [showAcked, setShowAcked] = useState(false)
  const nav = useContext(NavigationContext)
  const ackOf = (key: string) => acks.find(a => a.key === key)
  const active = alerts.filter(a => !ackOf(a.key)); const handled = alerts.filter(a => ackOf(a.key))
  const rows = showAcked ? [...active, ...handled] : active
  const review = (alert: Alert) => alert.target?.kind === 'repo' ? nav.navigate(alert.target.id) : alert.target?.kind === 'run' ? nav.openRun(alert.target.id) : alert.target?.kind === 'findings' ? nav.openFindings({ repositoryId: alert.target.id }) : onNavigate(alert.view || 'Scans')
  return <section className="panel feature-view"><div className="panel-head"><div><h2>Alert inbox</h2><p className="feature-note">{active.length} active · {handled.length} acknowledged or snoozed. Acknowledge what is handled; snoozed alerts return when the snooze ends.</p></div>{handled.length > 0 && <button className="secondary" onClick={() => setShowAcked(!showAcked)}>{showAcked ? 'Hide' : 'Show'} acknowledged ({handled.length})</button>}</div>
    <p className="feature-note">Webhook notifications for scan results are configured in {canConfigure ? <button className="text-button" onClick={() => onNavigate('Settings')}>Settings →</button> : 'Settings (admin).'}</p>
    <div className="alert-list">{rows.slice(0, shown).map(alert => { const ack = ackOf(alert.key); return <div className={`alert-row ${ack ? 'acked' : ''}`} key={alert.key}><span className={`alert-mark ${alert.severity.toLowerCase()}`}>{pretty(alert.severity)}</span><div><strong>{alert.title}</strong><small>{alert.detail}{alert.time ? ` · ${new Date(alert.time).toLocaleString()}` : ''}{ack && ` · ${ack.snoozeUntil ? `snoozed until ${new Date(ack.snoozeUntil).toLocaleString()}` : 'acknowledged'} by ${ack.actor}`}</small></div><button className="text-button" onClick={() => review(alert)}>Review</button>{canAck && (ack ? <button className="text-button" onClick={() => onUnack(alert.key)}>Reopen</button> : <><button className="text-button" onClick={() => onAck(alert.key)}>Acknowledge</button><button className="text-button" onClick={() => onAck(alert.key, 24)}>Snooze 24h</button></>)}</div> })}{rows.length > shown && <button className="secondary" onClick={() => setShown(shown + 25)}>Show more ({rows.length - shown} remaining)</button>}{!rows.length && <div className="empty-state">No active alerts. Aegis is quiet.</div>}</div></section>
}
