import React, { useState } from 'react'

export default function RoleMappingsView({ settings, canConfigure, onSave, pretty }: { settings: Record<string, any>; canConfigure: boolean; onSave: (value: Record<string, any>) => Promise<void>; pretty: (value: any) => string }) {
  const [form, setForm] = useState<Record<string, string>>(settings.roleMappings || {})
  const [group, setGroup] = useState('')
  const [mappedRole, setMappedRole] = useState('viewer')
  const save = () => { const next = { ...form, ...(group.trim() ? { [group.trim()]: mappedRole } : {}) }; setForm(next); setGroup(''); onSave({ ...settings, roleMappings: next }) }
  const remove = (key: string) => { const next = { ...form }; delete next[key]; setForm(next); onSave({ ...settings, roleMappings: next }) }
  return <section className="panel feature-view"><div className="panel-head"><div><h2>Role mappings</h2><p className="feature-note">Map identity-provider groups to AegisScan roles. Changes persist in runtime settings.</p></div></div>{canConfigure && <div className="user-add"><input aria-label="Identity group" value={group} onChange={e => setGroup(e.target.value)} placeholder="security-operators"/><select aria-label="Mapped role" value={mappedRole} onChange={e => setMappedRole(e.target.value)}><option value="viewer">Viewer</option><option value="operator">Operator</option><option value="admin">Admin</option></select><button className="primary" onClick={save}>Save mapping</button></div>}<div className="feature-table"><div className="feature-row feature-header"><span>Identity group</span><span>Role</span><span>Action</span></div>{Object.entries(form).map(([key, value]) => <div className="feature-row" key={key}><strong>{key}</strong><span className="pill success">{pretty(value)}</span>{canConfigure ? <button className="text-button" onClick={() => remove(key)}>Remove</button> : <span/>}</div>)}{!Object.keys(form).length && <div className="empty-state">No role mappings configured.</div>}</div></section>
}
