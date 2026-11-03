import { Button } from './button';
import { CardContent } from './card';
import { cn } from './cn';

interface PagerProps {
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  onPage: (pageNumber: number) => void;
  className?: string;
}

/** Previous / Next under a paged list; nothing when everything fits on one page. */
export function Pager({ pageNumber, pageSize, totalCount, onPage, className }: PagerProps) {
  const pages = Math.max(1, Math.ceil(totalCount / pageSize));
  if (pages <= 1) return null;

  // ARCH: the API pages the list; without these controls anything past the first page could never be opened.
  return (
    <CardContent className={cn('flex flex-wrap items-center justify-end gap-3', className)}>
      <Button variant="secondary" size="sm" disabled={pageNumber <= 1} onClick={() => onPage(pageNumber - 1)}>Previous</Button>
      <span className="text-xs text-ink-muted">Page {pageNumber} of {pages} · {totalCount.toLocaleString('en-AU')} in all</span>
      <Button variant="secondary" size="sm" disabled={pageNumber >= pages} onClick={() => onPage(pageNumber + 1)}>Next</Button>
    </CardContent>
  );
}