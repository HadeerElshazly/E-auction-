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

/**
 * The navigation and page icons, by name — one path each, drawn the same way as
 * the ones above. A name the set does not know draws the grid, never nothing.
 */
const paths: Record<string, string> = {
  grid: 'M3 3h7v7H3zm11 0h7v7h-7zM3 14h7v7H3zm11 0h7v7h-7z',
  file: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M8 13h8M8 17h6',
  gavel: 'm14 3 7 7m-9-5 7 7M3 21h11M6 18l10-10M5 11l5 5m7-14 5 5',
  bell: 'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
  user: 'M20 21v-2a7 7 0 0 0-14 0v2M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8',
  search: 'M21 21l-5-5M10 3a7 7 0 1 0 0 14 7 7 0 0 0 0-14',
  pin: 'M20 10c0 6-8 12-8 12S4 16 4 10a8 8 0 1 1 16 0zM12 7a3 3 0 1 0 0 6 3 3 0 0 0 0-6',
  area: 'M3 8V3h5M16 3h5v5M21 16v5h-5M8 21H3v-5M8 8h8v8H8z',
  chart: 'M3 3v18h18M7 16v-5M12 16V7M17 16V4',
  check: 'm5 12 4 4L19 6',
  clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18M12 7v5l3 2',
  shield: 'm12 2 9 4v6c0 5-9 10-9 10S3 17 3 12V6zM8 11l3 3 5-5',
  message: 'M21 11a9 9 0 0 1-9 9H3l1-5a9 9 0 1 1 17-4z',
  wallet: 'M3 6h17v15H3zM3 6V3h15v3M15 12h5v5h-5z',
  close: 'm6 6 12 12M6 18 18 6',
  download: 'M12 3v12m-5-5 5 5 5-5M4 17v4h16v-4',
  plus: 'M12 5v14M5 12h14',
  live: 'M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6M5 5a10 10 0 0 0 0 14M19 5a10 10 0 0 1 0 14',
  list: 'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
  lock: 'M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4',
  image: 'M3 4h18v16H3zM3 16l5-5 4 4 3-3 6 6M15 9a1 1 0 1 0 0-2 1 1 0 0 0 0 2',
}

export function Icon({ name, size = 20 }: { name: string; size?: number }) {
  return (
    <svg {...props} width={size} height={size}>
      <path d={paths[name] ?? paths.grid} />
    </svg>
  )
}
