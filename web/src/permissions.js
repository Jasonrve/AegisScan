const writableRoles = new Set(['admin', 'operator'])

export function canRunScans(role) {
  return writableRoles.has(String(role).toLowerCase())
}

export function canConfigure(role) {
  return String(role).toLowerCase() === 'admin'
}
