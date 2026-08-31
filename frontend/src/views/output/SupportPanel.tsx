import { useEffect, useState } from 'react';

interface SupportedGroup {
  title: string;
  items: string[];
}

interface SupportManifest {
  language: SupportedGroup[];
  library: SupportedGroup[];
  notSupported: string[];
}

/**
 * What this visualizer can and cannot run.
 *
 * Shown next to the diagnostics because that is where someone lands after hitting the
 * boundary. The plan calls out library-surface creep as a real risk, and the mitigation is to
 * make the edge visible instead of letting people map it by trial and error.
 *
 * Collapsed by default: it is reference material, not something to read on every run.
 */
export function SupportPanel() {
  const [manifest, setManifest] = useState<SupportManifest | null>(null);
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!open || manifest) return;
    let cancelled = false;
    fetch('/api/support')
      .then((r) => (r.ok ? r.json() : null))
      .then((m) => {
        if (!cancelled) setManifest(m);
      })
      .catch(() => {
        /* Reference material; if it cannot be fetched the panel simply stays empty. */
      });
    return () => {
      cancelled = true;
    };
  }, [open, manifest]);

  return (
    <div className="support">
      <button className="support-toggle" onClick={() => setOpen((o) => !o)} aria-expanded={open}>
        {open ? '▾' : '▸'} What C# is supported?
      </button>

      {open && !manifest && <div className="support-loading">Loading…</div>}

      {open && manifest && (
        <div className="support-body">
          {[...manifest.language, ...manifest.library].map((group) => (
            <section key={group.title} className="support-group">
              <h4 className="support-title">{group.title}</h4>
              <ul className="support-list">
                {group.items.map((item) => (
                  <li key={item}>{item}</li>
                ))}
              </ul>
            </section>
          ))}

          <section className="support-group">
            <h4 className="support-title support-title-off">Not supported</h4>
            <ul className="support-list support-list-off">
              {manifest.notSupported.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          </section>
        </div>
      )}
    </div>
  );
}
