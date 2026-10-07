/**
 * The header's icons. Inline SVG drawn in currentColor, so they take the button's
 * colour in every state and need no font or request. Decorative: the button around
 * each one carries the Arabic name as its aria-label and tooltip.
 */
const props = {
  width: 18,
  height: 18,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
  'aria-hidden': true,
}

export function BellIcon() {
  return (
    <svg {...props}>
      <path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9" />
      <path d="M10.3 21a1.94 1.94 0 0 0 3.4 0" />
    </svg>
  )
}

export function ApplicationsIcon() {
  return (
    <svg {...props}>
      <rect x="5" y="4" width="14" height="17" rx="2" />
      <path d="M9 4V3h6v1" />
      <path d="M9 10h6M9 14h6M9 18h3" />
    </svg>
  )
}

export function ProfileIcon() {
  return (
    <svg {...props}>
      <circle cx="12" cy="8" r="4" />
      <path d="M4 21a8 8 0 0 1 16 0" />
    </svg>
  )
}

export function SignOutIcon() {
  return (
    <svg {...props}>
      <path d="M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3" />
      <path d="M10 17l-5-5 5-5" />
      <path d="M5 12h11" />
    </svg>
  )
}

export function GavelIcon() {
  return (
    <svg {...props}>
      <path d="M14.5 3.5l6 6" />
      <path d="M11 7l6 6" />
      <path d="M12.75 5.25l-4.5 4.5 6 6 4.5-4.5" />
      <path d="M10.5 12L4 18.5a1.4 1.4 0 0 0 2 2l6.5-6.5" />
      <path d="M3 21h8" />
    </svg>
  )
}

export function MenuIcon() {
  return (
    <svg {...props}>
      <path d="M4 6h16M4 12h16M4 18h16" />
    </svg>
  )
}
