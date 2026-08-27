import { useState } from 'react'

interface Props {
  value: string | null
  label?: string
}

/**
 * A commit id, short until asked for. Forty characters is unreadable in a table
 * and seven is useless when you need to paste one, so it expands on click and
 * copies to the clipboard at the same time.
 */
export default function Sha({ value, label = 'commit' }: Props) {
  const [open, setOpen] = useState(false)
  const [copied, setCopied] = useState(false)

  if (!value) {
    return <span className="empty">—</span>
  }

  const show = async () => {
    setOpen((current) => !current)

    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    } catch {
      // Clipboard access is denied outside a secure context; the full id is
      // now on screen to be selected by hand, which is the point.
    }
  }

  return (
    <button
      type="button"
      className="sha"
      onClick={show}
      title={`${open ? 'Hide' : 'Show'} the full ${label} id, and copy it`}
      aria-label={`${label} ${value}`}
    >
      {open ? value : value.slice(0, 8)}
      {copied && <span className="hint"> copied</span>}
    </button>
  )
}
