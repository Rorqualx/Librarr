import $ from 'jquery';
import { applyMiddleware, combineReducers, createStore } from 'redux';
import { enableBatching } from 'redux-batched-actions';
import thunk from 'redux-thunk';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import createHandleActions from './Creators/createHandleActions';
import {
  clearLibraryImport,
  importAuthors,
  queueLookupAuthor,
  reducers,
  removeImportedFolders,
  setLibraryImportFolders,
  setLibraryImportValue
} from './libraryImportActions';

// Every request is a Deferred the test settles by hand, because the bugs this
// file pins down are all about ordering: what a lookup does when it lands
// after its row has gone, and what the page keeps once an import answers.
vi.mock('Utilities/createAjaxRequest', () => ({ default: vi.fn() }));

const HARPER = '/books/Harper Lee';
const LEE = '/books/Lee, Harper';
const ORWELL = '/books/George Orwell';

const AUTHOR = { foreignAuthorId: 'OL1A', authorName: 'Harper Lee' };
const OTHER = { foreignAuthorId: 'OL2A', authorName: 'Someone Else' };

let requests = [];

function createTestStore() {
  const rootFolders = {
    items: [{
      id: 1,
      path: '/books',
      unmappedFolders: [HARPER, LEE, ORWELL].map((path) => ({ path, name: path.slice(7) }))
    }]
  };

  return createStore(
    enableBatching(combineReducers({
      libraryImport: reducers,
      // Stand-ins that still run the shared UPDATE_ITEM reducer, so what the
      // import writes outside its own section is checked for real.
      settings: createHandleActions({}, { rootFolders }, 'settings'),
      authors: createHandleActions({}, { items: [] }, 'authors')
    })),
    applyMiddleware(thunk)
  );
}

function seed(store) {
  const rootFolder = store.getState().settings.rootFolders.items[0];

  store.dispatch(setLibraryImportFolders({
    rootFolderId: rootFolder.id,
    unmappedFolders: rootFolder.unmappedFolders
  }));
}

function rows(store) {
  return store.getState().libraryImport.items;
}

function row(store, id) {
  return rows(store).find((item) => item.id === id);
}

// Queue a lookup for a row and answer it.
function lookup(store, id, results) {
  store.dispatch(queueLookupAuthor({ id, term: 'x' }));
  requests[requests.length - 1].deferred.resolve(results);
}

beforeEach(() => {
  requests = [];

  createAjaxRequest.mockImplementation((options) => {
    const deferred = $.Deferred();

    requests.push({ options, deferred });

    return { request: deferred.promise(), abortRequest: vi.fn() };
  });
});

describe('libraryImportActions', () => {
  let store = null;

  beforeEach(() => {
    store = createTestStore();
    // The lookup queue is module state; clearing resets it between tests.
    store.dispatch(clearLibraryImport());
    seed(store);
  });

  describe('lookups', () => {
    it('pre-selects the first match', () => {
      lookup(store, HARPER, [AUTHOR, OTHER]);

      expect(row(store, HARPER).selectedAuthor).toEqual(AUTHOR);
    });

    // The tick lives in LibraryImport, which only keeps one on a row that has
    // a selectedAuthor -- so clearing it here is what unticks the row.
    it('drops the current match as soon as the row is searched again', () => {
      lookup(store, HARPER, [AUTHOR]);

      store.dispatch(queueLookupAuthor({ id: HARPER, term: 'someone else' }));

      expect(row(store, HARPER).selectedAuthor).toBeUndefined();
      expect(row(store, HARPER).isFetching).toBe(true);
    });

    it('does not bring back a row that was removed while its lookup was in flight', () => {
      store.dispatch(queueLookupAuthor({ id: HARPER, term: 'Harper Lee' }));
      store.dispatch(removeImportedFolders({ paths: [HARPER], skipped: [] }));

      requests[0].deferred.resolve([AUTHOR]);

      expect(rows(store).map((item) => item.id)).toEqual([LEE, ORWELL]);
    });

    it('does not bring back a removed row when its lookup fails late', () => {
      store.dispatch(queueLookupAuthor({ id: HARPER, term: 'Harper Lee' }));
      store.dispatch(removeImportedFolders({ paths: [HARPER], skipped: [] }));

      requests[0].deferred.reject({ status: 500 });

      expect(rows(store).map((item) => item.id)).toEqual([LEE, ORWELL]);
    });
  });

  describe('importing', () => {
    function importAll(response) {
      lookup(store, HARPER, [AUTHOR]);
      lookup(store, LEE, [AUTHOR]);
      lookup(store, ORWELL, [OTHER]);

      store.dispatch(importAuthors({ ids: [HARPER, LEE, ORWELL] }));

      const request = requests[requests.length - 1];

      request.deferred.resolve(response);

      return request;
    }

    const RESPONSE = {
      imported: [{ id: 10, path: HARPER, ...AUTHOR }],
      skipped: [
        { path: LEE, foreignAuthorId: 'OL1A', reason: 'duplicate', message: 'dup' },
        { path: ORWELL, foreignAuthorId: 'OL2A', reason: 'exists', message: 'already' }
      ]
    };

    it('binds each author to its folder', () => {
      const request = importAll(RESPONSE);

      expect(request.options.url).toBe('/author/import');
      expect(JSON.parse(request.options.data).map((author) => author.path))
        .toEqual([HARPER, LEE, ORWELL]);
    });

    it('removes imported rows and says why each of the others stayed', () => {
      importAll(RESPONSE);

      expect(rows(store).map((item) => item.id)).toEqual([LEE, ORWELL]);
      expect(row(store, LEE).importOutcome).toEqual({ reason: 'duplicate', message: 'dup' });
      expect(row(store, ORWELL).importOutcome).toEqual({ reason: 'exists', message: 'already' });
      expect(store.getState().libraryImport.skippedCount).toBe(2);
      expect(store.getState().libraryImport.isImporting).toBe(false);
      expect(store.getState().authors.items.map((author) => author.id)).toEqual([10]);
    });

    // Otherwise leaving the page and coming back offers the same folder again.
    it('takes imported folders out of the root folder\'s unmapped list', () => {
      importAll(RESPONSE);

      expect(store.getState().settings.rootFolders.items[0].unmappedFolders.map((f) => f.path))
        .toEqual([LEE, ORWELL]);
    });

    it('clears a row\'s outcome once a different author is picked for it', () => {
      importAll(RESPONSE);

      store.dispatch(setLibraryImportValue({ id: LEE, selectedAuthor: OTHER }));

      expect(row(store, LEE).importOutcome).toBeNull();
      expect(row(store, ORWELL).importOutcome).not.toBeNull();
    });

    it('does not carry outcomes over from a previous attempt', () => {
      importAll(RESPONSE);

      store.dispatch(removeImportedFolders({ paths: [], skipped: [] }));

      expect(row(store, LEE).importOutcome).toBeNull();
      expect(store.getState().libraryImport.skippedCount).toBe(0);
    });

    it('does not spend a search on a row that was imported while it waited in the queue', () => {
      lookup(store, HARPER, [AUTHOR]);

      // LEE goes in flight and holds the single slot; ORWELL waits behind it.
      store.dispatch(queueLookupAuthor({ id: LEE, term: 'Lee' }));
      store.dispatch(queueLookupAuthor({ id: ORWELL, term: 'Orwell' }));
      store.dispatch(setLibraryImportValue({ id: ORWELL, selectedAuthor: OTHER }));

      store.dispatch(importAuthors({ ids: [ORWELL] }));
      requests[requests.length - 1].deferred.resolve({
        imported: [{ id: 11, path: ORWELL, ...OTHER }],
        skipped: []
      });

      const before = requests.length;

      requests[1].deferred.resolve([AUTHOR]);

      expect(requests.length).toBe(before);
      expect(rows(store).map((item) => item.id)).toEqual([HARPER, LEE]);
    });

    it('keeps every row and records the error when the request fails', () => {
      lookup(store, HARPER, [AUTHOR]);

      store.dispatch(importAuthors({ ids: [HARPER] }));
      requests[requests.length - 1].deferred.reject({ status: 500 });

      expect(rows(store)).toHaveLength(3);
      expect(store.getState().libraryImport.importError).toEqual({ status: 500 });
    });
  });
});
