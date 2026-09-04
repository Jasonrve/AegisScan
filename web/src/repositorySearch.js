export function filterRepositories(repositories, query) {
  const normalized = query.trim().toLowerCase()
  return repositories.filter(([name]) => name.toLowerCase().includes(normalized))
}
