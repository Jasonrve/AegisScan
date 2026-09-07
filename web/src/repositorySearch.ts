export function filterRepositories(repositories: string[][], query: string): string[][] {
  const normalized = query.trim().toLowerCase()
  return repositories.filter(([name]) => name.toLowerCase().includes(normalized))
}
