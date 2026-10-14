'use client';

import type { WorkspaceContext, WorkspaceContextItem } from '../workspace-context';
import styles from './ApplicationWorkspace.module.css';

interface ApplicationWorkspaceProps {
  context: WorkspaceContext;
}

function ContextItem({ item, emphasis = false }: { item: WorkspaceContextItem; emphasis?: boolean }) {
  return (
    <div className={emphasis ? `${styles.item} ${styles.currentItem}` : styles.item}>
      <span className={styles.itemTitle}>{item.title}</span>
      <span className={styles.itemValue}>{String(item.value)}</span>
    </div>
  );
}

// ┌─ :: ARCHITECTURE :: Opaque workspace context ──────────────────────────────────────────────────
// │ Why : If the Shell understood business data it would become a distributed-monolith hub.
// │ How : The selected record travels as title / value pairs that the Shell only shows and hands
// │       on; a receiving MFE re-reads anything authoritative from its own BFF.
// │ Ref : Features 1.5.2, 3.1 | Shell-Requirements.md section 3
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function ApplicationWorkspace({ context }: ApplicationWorkspaceProps) {
  const persistent = context?.persistentContext ?? [];
  const current = context?.currentContext ?? [];
  const retained = context?.retainedContext ?? [];
  const hasVisibleContext = persistent.length > 0 || current.length > 0;

  return (
    <section className={styles.workspace} aria-label="Application workspace context">
          <div className={styles.headingBlock}>
              <div className={styles.workspaceIcon} aria-hidden="true">
                  <span className={styles.workspaceIconMark}>▦</span>
              </div>

              <div className={styles.headingContent}>
                  <span className={styles.eyebrow}>APPLICATION WORKSPACE</span>

                  {!hasVisibleContext ? (
                      <span className={styles.hint}>
                          Select an operation from the menu
                      </span>
                  ) : (
                      <span className={styles.contextDescription}>
                          Current application context
                      </span>
                  )}
              </div>
          </div>

      {hasVisibleContext && (
        <div className={styles.visibleContext}>
          {persistent.map((item, index) => (
            <ContextItem key={`persistent-${item.title}-${index}`} item={item} />
          ))}
          {current.map((item, index) => (
            <ContextItem key={`current-${item.title}-${index}`} item={item} emphasis />
          ))}
        </div>
      )}

      {/* retainedContext deliberately remains invisible.  The Shell stores and
          relays it mechanically, but only persistent/current context is shown. */}
      <span className={styles.contextCount} aria-label={`${retained.length} retained context items`}>
        {retained.length > 0 ? `${retained.length} retained` : ''}
      </span>
    </section>
  );
}