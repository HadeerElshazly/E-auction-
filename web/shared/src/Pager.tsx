import { useState } from 'react'

/** Rows per page on every grid; the services cut the page (?skip=&take=). */
export const PAGE_SIZE = 10

/** A list endpoint's answer: one page and the count behind it. */
export interface PageOf<T> {
  items: T[]
  total: number
  skip: number
  take: number
}

/**
 * The page a grid is on, and the query string that asks the service for it. The
 * caller resets it to the first page when a filter or the search changes.
 */
export function usePage(size = PAGE_SIZE) {
  const [page, setPage] = useState(0)
  return { page, setPage, size, query: `skip=${page * size}&take=${size}` }
}

/**
 * The pages of a grid, shown only when there is more than one. Filtering and
 * paging are done by the service; this only says where the reader is and moves.
 */
export function Pager({
  page,
  total,
  size = PAGE_SIZE,
  noun = 'سجل',
  onPage,
}: {
  page: number
  total: number
  size?: number
  noun?: string
  onPage: (page: number) => void
}) {
  const pages = Math.max(1, Math.ceil(total / size))
  if (total <= size) return null
  const from = page * size + 1
  const to = Math.min(total, (page + 1) * size)
  return (
    <div className="pager" data-testid="pager">
      <span className="muted small">
        <span className="num">{from}–{to}</span> من <span className="num">{total}</span> {noun}
      </span>
      <span className="grow" />
      <button className="ghost" disabled={page === 0} onClick={() => onPage(0)} aria-label="الصفحة الأولى">
        «
      </button>
      <button className="ghost" disabled={page === 0} onClick={() => onPage(page - 1)}>
        السابق
      </button>
      <span className="small">
        صفحة <span className="num">{page + 1}</span> من <span className="num">{pages}</span>
      </span>
      <button className="ghost" disabled={page >= pages - 1} onClick={() => onPage(page + 1)}>
        التالي
      </button>
      <button className="ghost" disabled={page >= pages - 1} onClick={() => onPage(pages - 1)} aria-label="الصفحة الأخيرة">
        »
      </button>
    </div>
  )
}
