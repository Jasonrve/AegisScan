const writableRoles = new Set<string>(['admin', 'operator'])

export function canRunScans(role: string): boolean {
  return writableRoles.has(String(role).toLowerCase())
}

export function canConfigure(role: string): boolean {
  return String(role).toLowerCase() === 'admin'
}
