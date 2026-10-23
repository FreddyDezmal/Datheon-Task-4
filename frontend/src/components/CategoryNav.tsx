import { useEffect, useRef } from 'react';
import type { Category } from '../types/product';

interface CategoryNavProps {
  categories: readonly Category[];
  selected: string;
  onSelect: (slug: string) => void;
}

/**
 * "All products", standalone categories, then each group (e.g. Clothing) followed by its
 * sub-categories, which are drawn as a joined cluster so the grouping is visible.
 */
export function CategoryNav({ categories, selected, onSelect }: CategoryNavProps) {
  const standalone = categories.filter((c) => !c.isGroup && c.parentSlug === null);
  const groups = categories.filter((c) => c.isGroup);
  const trackRef = useRef<HTMLDivElement>(null);

  // On narrow screens the chip row scrolls; keep the active chip visible. This scrolls the row
  // itself rather than calling scrollIntoView, which in Chrome also moves the starting point for
  // the next Tab press, making a keyboard user's first Tab skip the header.
  useEffect(() => {
    const track = trackRef.current;
    const active = track?.querySelector<HTMLElement>('[aria-pressed="true"]');
    if (!track || !active) return;
    const trackBox = track.getBoundingClientRect();
    const chipBox = active.getBoundingClientRect();
    const margin = 16;
    if (chipBox.left < trackBox.left) track.scrollLeft -= trackBox.left - chipBox.left + margin;
    else if (chipBox.right > trackBox.right) track.scrollLeft += chipBox.right - trackBox.right + margin;
  }, [selected, categories]);

  const chip = (slug: string, label: string, variant?: 'child') => (
    <button
      key={slug}
      type="button"
      className={`chip${variant === 'child' ? ' chip--child' : ''}`}
      aria-pressed={selected === slug}
      onClick={() => onSelect(slug)}
    >
      {label}
    </button>
  );

  return (
    <nav className="category-nav" aria-label="Product categories">
      <div ref={trackRef} className="category-nav__track">
        {chip('all', 'All products')}
        {standalone.map((c) => chip(c.slug, c.name))}
        {groups.map((group) => (
          <div key={group.slug} className="chip-group" role="group" aria-label={group.name}>
            {chip(group.slug, group.name)}
            {categories
              .filter((c) => c.parentSlug === group.slug)
              .map((c) => chip(c.slug, c.name.replace(new RegExp(`\\s*${group.name}$`, 'i'), '') || c.name, 'child'))}
          </div>
        ))}
      </div>
    </nav>
  );
}
