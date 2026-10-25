import { describe, expect, it, vi } from 'vitest';
import createDeepEqualSelector from './createDeepEqualSelector';

// reselect 5 dropped the `defaultMemoize` export. Importing it gave
// createSelectorCreator `undefined`, which broke every page at load while
// the build stayed green. These selectors must load and memoize on deep
// equality.
describe('createDeepEqualSelector', () => {
  it('returns the cached result for a deep-equal input', () => {
    const combiner = vi.fn((items) => items.map((i) => i.id));
    const selector = createDeepEqualSelector((state) => state.items, combiner);

    const first = selector({ items: [{ id: 1 }, { id: 2 }] });
    const second = selector({ items: [{ id: 1 }, { id: 2 }] });

    expect(second).toBe(first);
    expect(combiner).toHaveBeenCalledTimes(1);
  });

  it('recomputes when the input changes', () => {
    const combiner = vi.fn((items) => items.length);
    const selector = createDeepEqualSelector((state) => state.items, combiner);

    expect(selector({ items: [1] })).toBe(1);
    expect(selector({ items: [1, 2] })).toBe(2);
    expect(combiner).toHaveBeenCalledTimes(2);
  });
});
