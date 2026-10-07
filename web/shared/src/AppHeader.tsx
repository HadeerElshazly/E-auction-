import type { ReactNode } from 'react'
import type { Session } from './auth'
import { MenuIcon, SignOutIcon } from './Icons'

/** Each role's name as the person holding it would say it. */
const roleAr: Array<[string, string]> = [
  ['bidder', 'مزايد'],
  ['auction-admin', 'إدارة المزادات'],
  ['award-committee', 'لجنة الترسية'],
  ['operator', 'موظف القاعة'],
  ['inquiries', 'الاستفسارات'],
  ['reporting', 'التقارير'],
  ['auditor', 'المراجعة'],
]

/** The signed-in person's roles, in Arabic, in a fixed order. */
export function rolesAr(session: Session): string[] {
  return roleAr.filter(([r]) => session.roles.includes(r)).map(([, ar]) => ar)
}

function initials(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean)
  return (parts[0]?.[0] ?? '') + (parts.length > 1 ? parts[parts.length - 1]![0] : '')
}

/**
 * The top bar, one for both portals: the portal's name, then whatever this portal
 * offers this person (passed in as <paramref name="actions"/>), then who is signed
 * in and the way out. What differs between a bidder and a member of staff is the
 * buttons and the role line — never the bar itself, so a change here reaches every
 * user of every portal at once.
 */
export function AppHeader({
  title,
  session,
  actions,
  extra,
  onSignIn,
  signInLabel = 'تسجيل الدخول',
  onSignOut,
  onToggleMenu,
  menuOpen,
}: {
  title: string
  session: Session | null
  /** Portal- and role-specific buttons, shown before the user badge. */
  actions?: ReactNode
  /** Anything else that belongs beside the user, such as a clerk's id. */
  extra?: ReactNode
  onSignIn?: () => void
  signInLabel?: string
  onSignOut: () => void
  /** Present on a portal with a side menu: the button that collapses it. */
  onToggleMenu?: () => void
  menuOpen?: boolean
}) {
  const name = session ? session.nameAr ?? session.name ?? '' : ''
  const roles = session ? rolesAr(session) : []

  return (
    <header className="bar" data-testid="app-header">
      {onToggleMenu && (
        <button
          className={`icon-btn${menuOpen ? '' : ' on'}`}
          aria-label={menuOpen ? 'طي القائمة' : 'فتح القائمة'}
          title={menuOpen ? 'طي القائمة' : 'فتح القائمة'}
          aria-expanded={menuOpen}
          onClick={onToggleMenu}
        >
          <MenuIcon />
        </button>
      )}
      <h1>{title}</h1>
      <span className="grow" />

      {session ? (
        <>
          {actions}
          {extra}
          <div className="user-badge" data-testid="user-badge">
            <span className="avatar" aria-hidden="true">{initials(name)}</span>
            <span className="user-lines">
              <span className="user-name">{name}</span>
              <span className="user-role">
                {roles.join(' · ')}
                {session.nationalId && <span className="ltr"> · {session.nationalId}</span>}
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
    </header>
  )
}
