import type { ReactNode } from 'react';

interface StatusMessageProps {
  title: string;
  children?: ReactNode;
  action?: ReactNode;
  tone?: 'neutral' | 'error';
}

/** Empty and error states: say what happened and offer the next step. */
export function StatusMessage({ title, children, action, tone = 'neutral' }: StatusMessageProps) {
  return (
    <div className={`status-message status-message--${tone}`} role={tone === 'error' ? 'alert' : undefined}>
      <h2 className="status-message__title">{title}</h2>
      {children && <div className="status-message__body">{children}</div>}
      {action && <div className="status-message__action">{action}</div>}
    </div>
  );
}
