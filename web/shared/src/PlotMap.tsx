import { useEffect, useRef } from 'react'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { MAP_TILES } from './endpoints'

export interface MapPoint {
  lat: number
  lng: number
  label?: string
}

/** Riyadh, for a map with nothing on it yet. */
const DEFAULT_CENTRE: [number, number] = [24.7136, 46.6753]

// A drawn pin rather than Leaflet's image icon: the image's address is guessed at
// run time and a bundler breaks the guess, which shows as a map with no markers.
const pin = (picked: boolean) =>
  L.divIcon({
    className: `plot-pin${picked ? ' picked' : ''}`,
    html: '<span></span>',
    iconSize: [22, 22],
    iconAnchor: [11, 22],
  })

/**
 * A map of an auction's plots — and, with <paramref name="onPick"/>, the way an
 * administrator sets where a plot is: click the map and the point is the plot's
 * location, instead of typing coordinates copied from somewhere else.
 *
 * Tiles come from {@link MAP_TILES}, the one tile server the pages' policy allows.
 */
export function PlotMap({
  points,
  picked,
  onPick,
  height = 320,
}: {
  points: MapPoint[]
  /** The point being chosen, drawn apart from the plots already placed. */
  picked?: MapPoint | null
  onPick?: (point: MapPoint) => void
  height?: number
}) {
  const box = useRef<HTMLDivElement>(null)
  const map = useRef<L.Map | null>(null)
  const layer = useRef<L.LayerGroup | null>(null)
  const pickRef = useRef(onPick)
  pickRef.current = onPick

  // The map, once.
  useEffect(() => {
    if (!box.current || map.current) return
    const m = L.map(box.current, { zoomControl: true, attributionControl: true })
    L.tileLayer(MAP_TILES.url, { maxZoom: 19, attribution: MAP_TILES.attribution }).addTo(m)
    m.setView(DEFAULT_CENTRE, 11)
    m.on('click', (e: L.LeafletMouseEvent) =>
      pickRef.current?.({ lat: round(e.latlng.lat), lng: round(e.latlng.lng) }),
    )
    layer.current = L.layerGroup().addTo(m)
    map.current = m
    return () => {
      m.remove()
      map.current = null
    }
  }, [])

  // The pins, whenever they change; the view fitted to them.
  useEffect(() => {
    const m = map.current
    const g = layer.current
    if (!m || !g) return
    g.clearLayers()
    const all: L.LatLngExpression[] = []
    for (const p of points) {
      const marker = L.marker([p.lat, p.lng], { icon: pin(false) })
      if (p.label) marker.bindTooltip(p.label)
      marker.addTo(g)
      all.push([p.lat, p.lng])
    }
    if (picked) {
      L.marker([picked.lat, picked.lng], { icon: pin(true) }).addTo(g)
      all.push([picked.lat, picked.lng])
    }
    if (all.length === 1) m.setView(all[0]!, Math.max(m.getZoom(), 15))
    else if (all.length > 1) m.fitBounds(L.latLngBounds(all), { padding: [30, 30], maxZoom: 16 })
  }, [points, picked])

  return (
    <div
      ref={box}
      className={`plot-map${onPick ? ' picking' : ''}`}
      style={{ height }}
      role="application"
      aria-label={onPick ? 'خريطة — انقر لتحديد موقع القطعة' : 'خريطة مواقع القطع'}
    />
  )
}

/** Six decimals: about ten centimetres, well past what a plot boundary needs. */
function round(x: number): number {
  return Math.round(x * 1e6) / 1e6
}
