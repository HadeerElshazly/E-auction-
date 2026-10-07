import { useEffect, useState, type ReactNode } from 'react'
import type { Session } from './auth'
import { rolesAr } from './AppHeader'
import { Icon, MenuIcon, SignOutIcon } from './Icons'

export interface NavItem {
  /** The route it opens, without the '#'. */
  to: string
  label: string
  icon: string
  /** A count beside the label — unread notices, decisions waiting. */
  badge?: number
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  return (parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1]![0] : '')
}

/**
 * The frame every page of both portals sits in: the sidebar with the brand and
 * this person's workspace, the top bar with where they are and who they are, the
 * page itself, and the footer.
 *
 * One component for both portals and every role. What differs between a bidder,
 * a committee member and an auditor is the list of items passed in — never the
 * frame — so the product reads as one product, and a change here reaches all of it.
 *
 * The sidebar folds to its icons on a desktop (remembered per browser) and becomes
 * a drawer on a phone.
 */
export function AppShell({
  brand,
  brandSub,
  workspace = 'مساحة العمل',
  nav,
  active,
  crumb,
  session,
  onSignIn,
  signInLabel = 'تسجيل الدخول',
  onSignOut,
  tools,
  sidebarFoot,
  footer,
  children,
}: {
  brand: string
  brandSub: string
  workspace?: string
  nav: NavItem[]
  /** The route of the item to show as current. */
  active: string
  /** The current page's name, for the breadcrumb. */
  crumb: string
  session: Session | null
  onSignIn?: () => void
  signInLabel?: string
  onSignOut: () => void
  /** Buttons before the account: the bell, a portal's own shortcuts. */
  tools?: ReactNode
  sidebarFoot?: ReactNode
  footer?: ReactNode
  children: ReactNode
}) {
  const [folded, setFolded] = useState(() => {
    try {
      return localStorage.getItem('shell.sidebar') === 'folded'
    } catch {
      return false
    }
  })
  const [drawer, setDrawer] = useState(false)

  // A route change closes the phone drawer: the person has chosen where to go.
  useEffect(() => {
    const close = () => setDrawer(false)
    window.addEventListener('hashchange', close)
    return () => window.removeEventListener('hashchange', close)
  }, [])

  const toggle = () => {
    if (window.matchMedia('(max-width: 900px)').matches) {
      setDrawer((d) => !d)
      return
    }
    setFolded((f) => {
      try {
        localStorage.setItem('shell.sidebar', f ? 'open' : 'folded')
      } catch {
        // Not remembered; still folds.
      }
      return !f
    })
  }

  const name = session ? session.nameAr ?? session.name ?? '' : ''
  const roles = session ? rolesAr(session) : []

  return (
    <div className={`app-shell${folded ? ' folded' : ''}${drawer ? ' drawer-open' : ''}`}>
      <aside className="sidebar" aria-label="القائمة الرئيسية">
        <a className="brand" href={`#${nav[0]?.to ?? ''}`}>
          <span className="brand-mark" aria-hidden="true">
            <Icon name="grid" size={20} />
          </span>
          <span className="brand-text">
            {brand}
            <small>{brandSub}</small>
          </span>
        </a>
        <div className="nav-label">{workspace}</div>
        <nav className="side-nav" data-testid="nav">
          {nav.map((item) => (
            <a
              key={item.to}
              href={`#${item.to}`}
              className={`nav-item${active === item.to ? ' active' : ''}`}
              aria-current={active === item.to ? 'page' : undefined}
              title={folded ? item.label : undefined}
              data-testid={`nav-${item.to}`}
            >
              <Icon name={item.icon} />
              <span className="nav-text">{item.label}</span>
              {!!item.badge && <span className="nav-badge num">{item.badge > 99 ? '99+' : item.badge}</span>}
            </a>
          ))}
        </nav>
        {sidebarFoot && <div className="sidebar-bottom">{sidebarFoot}</div>}
      </aside>
      {drawer && <div className="drawer-scrim" onClick={() => setDrawer(false)} aria-hidden="true" />}

      <div className="workspace">
        <header className="topbar" data-testid="app-header">
          <button
            className="icon-btn"
            onClick={toggle}
            aria-label={folded || !drawer ? 'القائمة' : 'إغلاق القائمة'}
            title="القائمة"
            aria-expanded={!folded}
          >
            <MenuIcon />
          </button>
          <div className="breadcrumb">
            الرئيسية <span aria-hidden="true">/</span> <strong>{crumb}</strong>
          </div>
          <span className="grow" />
          <div className="header-tools">
            {tools}
            {session ? (
              <>
                <div className="user-badge" data-testid="user-badge">
                  <span className="avatar" aria-hidden="true">{initials(name)}</span>
                  <span className="user-lines">
                    <span className="user-name">{name}</span>
                    <span className="user-role">
                      {roles.join(' · ')}
                      {session.nationalId && <>{roles.length > 0 && ' · '}<span className="ltr">{session.nationalId}</span></>}
                    </span>
                  </span>
                </div>
                <button className="icon-btn" onClick={onSignOut} aria-label="خروج" title="خروج">
                  <SignOutIcon />
                </button>
              </>
            ) : (
              onSignIn && (
                <button className="primary" onClick={onSignIn}>
                  {signInLabel}
                </button>
              )
            )}
          </div>
        </header>

        <main className="page" id="main" tabIndex={-1}>
          {children}
        </main>
        {footer && <footer className="page-footer">{footer}</footer>}
      </div>
    </div>
  )
}

/** A page's heading: the context line, the title, a sentence, and its main action. */
export function PageHead({
  eyebrow,
  title,
  sub,
  action,
}: {
  eyebrow?: string
  title: ReactNode
  sub?: ReactNode
  action?: ReactNode
}) {
  return (
    <div className="page-head">
      <div className="grow">
        {eyebrow && <div className="eyebrow">{eyebrow}</div>}
        <h1>{title}</h1>
        {sub && <p>{sub}</p>}
      </div>
      {action}
    </div>
  )
}

/** The strip of figures under a page heading. */
export function Stats({ items }: { items: Array<{ label: string; value: ReactNode; icon: string; unit?: string }> }) {
  return (
    <section className="stats" data-testid="stats">
      {items.map((s) => (
        <div className="stat-cell" key={s.label}>
          <div className="stat-head">
            {s.label}
            <Icon name={s.icon} size={18} />
          </div>
          <div className="stat-figure num">
            {s.value}
            {s.unit && <small>{s.unit}</small>}
          </div>
        </div>
      ))}
    </section>
  )
}
