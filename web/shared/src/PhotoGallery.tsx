import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'

export interface GalleryImage {
  id: string
  url: string
  title: string
}

/**
 * معرض الصور — the land's photos as small squares; a tap opens one full size, with
 * the others a swipe of the arrows away.
 *
 * An auction's public documents are a mix of photos and PDFs (a plan, a survey), and
 * nothing on the auction says which is which. Every one is offered as an image and
 * any that the browser cannot draw is dropped quietly, so a PDF never shows as a
 * broken picture.
 */
export function PhotoGallery({
  images,
  size = 104,
  empty,
  extra,
}: {
  images: GalleryImage[]
  /** The side of each square, in pixels. */
  size?: number
  /** What to show when there is no photo at all. */
  empty?: ReactNode
  /** A last square after the photos — an upload button, for staff. */
  extra?: ReactNode
}) {
  const [broken, setBroken] = useState<Set<string>>(new Set())
  const shown = useMemo(() => images.filter((i) => !broken.has(i.id)), [images, broken])
  const [open, setOpen] = useState<number | null>(null)
  const drop = (id: string) => setBroken((b) => new Set(b).add(id))

  if (shown.length === 0 && !extra) return <>{empty ?? null}</>

  return (
    <>
      <div className="gallery-grid" data-testid="gallery" role="list" aria-label="صور القطعة">
        {shown.map((img, i) => (
          <button
            key={img.id}
            role="listitem"
            className="gallery-square"
            style={{ width: size, height: size }}
            onClick={() => setOpen(i)}
            title={img.title}
            aria-label={`عرض الصورة: ${img.title}`}
          >
            <img src={img.url} alt="" loading="lazy" onError={() => drop(img.id)} />
          </button>
        ))}
        {extra && (
          <div className="gallery-square extra" style={{ width: size, height: size }}>
            {extra}
          </div>
        )}
      </div>
      {open !== null && shown.length > 0 && (
        <Lightbox
          images={shown}
          index={Math.min(open, shown.length - 1)}
          onIndex={setOpen}
          onClose={() => setOpen(null)}
        />
      )}
    </>
  )
}

/** One photo full size over the page; Esc closes it, the arrow keys move along. */
export function Lightbox({
  images,
  index,
  onIndex,
  onClose,
}: {
  images: GalleryImage[]
  index: number
  onIndex: (i: number) => void
  onClose: () => void
}) {
  const current = images[index]!
  const many = images.length > 1
  // In a right-to-left page «التالية» sits on the left and is the left arrow key.
  const next = () => onIndex((index + 1) % images.length)
  const prev = () => onIndex((index - 1 + images.length) % images.length)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
      else if (e.key === 'ArrowLeft' && many) next()
      else if (e.key === 'ArrowRight' && many) prev()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  return createPortal(
    <div className="lightbox" role="dialog" aria-modal="true" aria-label={current.title} onClick={onClose}>
      <figure onClick={(e) => e.stopPropagation()}>
        <img src={current.url} alt={current.title} />
        <figcaption>
          <bdi>{current.title}</bdi>
          {many && (
            <>
              {' · '}
              <bdi className="num">
                {index + 1} / {images.length}
              </bdi>
            </>
          )}
        </figcaption>
      </figure>
      <button className="lightbox-close" aria-label="إغلاق" onClick={onClose}>
        ✕
      </button>
      {many && (
        <>
          <button
            className="lightbox-nav prev"
            aria-label="الصورة السابقة"
            onClick={(e) => {
              e.stopPropagation()
              prev()
            }}
          >
            ›
          </button>
          <button
            className="lightbox-nav next"
            aria-label="الصورة التالية"
            onClick={(e) => {
              e.stopPropagation()
              next()
            }}
          >
            ‹
          </button>
        </>
      )}
    </div>,
    document.body,
  )
}
