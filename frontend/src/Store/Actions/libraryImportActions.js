import _ from 'lodash';
import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import getNewAuthor from 'Utilities/Author/getNewAuthor';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import getSectionState from 'Utilities/State/getSectionState';
import updateSectionState from 'Utilities/State/updateSectionState';
import { set, updateItem } from './baseActions';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'libraryImport';

// Every row needs its own OpenLibrary lookup, and a root folder with a few
// hundred author folders would otherwise fire a few hundred searches the moment
// the page mounts. OL's search API is the metered one (covers by CoverID/OLID
// are exempt, search is not), so lookups go through a queue that keeps at most
// CONCURRENT_LOOKUPS in flight and the rest waiting.
const CONCURRENT_LOOKUPS = 1;

const queue = [];
let inFlight = 0;
let abortCurrentLookups = [];

// Bumped whenever the queue is torn down. Aborting an in-flight request still
// runs its completion handler, and without this the handler would decrement a
// counter that has already been reset — leaving inFlight negative and letting
// the next visit to the page run more lookups at once than intended.
let generation = 0;

function clearQueue() {
  generation++;

  queue.splice(0, queue.length);

  abortCurrentLookups.forEach((abort) => abort());
  abortCurrentLookups = [];
  inFlight = 0;
}

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  isImporting: false,
  importError: null,
  skippedCount: 0,
  rootFolderId: 0,
  items: [],

  // Mirrors searchActions' explicit-only stance: importing an existing shelf
  // should adopt what is already on disk, not immediately fan out and start
  // grabbing every other book the author ever wrote.
  defaults: {
    monitor: 'none',
    monitorNewItems: 'none',
    qualityProfileId: 0,
    metadataProfileId: 0,
    tags: []
  }
};

export const persistState = [
  'libraryImport.defaults'
];

//
// Action Types

export const SET_LIBRARY_IMPORT_FOLDERS = 'libraryImport/setLibraryImportFolders';
export const REMOVE_IMPORTED_FOLDERS = 'libraryImport/removeImportedFolders';
export const QUEUE_LOOKUP_AUTHOR = 'libraryImport/queueLookupAuthor';
export const START_LOOKUP_AUTHOR = 'libraryImport/startLookupAuthor';
export const SET_LIBRARY_IMPORT_VALUE = 'libraryImport/setLibraryImportValue';
export const SET_LIBRARY_IMPORT_DEFAULT = 'libraryImport/setLibraryImportDefault';
export const IMPORT_AUTHORS = 'libraryImport/importAuthors';
export const CLEAR_LIBRARY_IMPORT = 'libraryImport/clearLibraryImport';

//
// Action Creators

export const setLibraryImportFolders = createAction(SET_LIBRARY_IMPORT_FOLDERS);
export const removeImportedFolders = createAction(REMOVE_IMPORTED_FOLDERS);
export const queueLookupAuthor = createThunk(QUEUE_LOOKUP_AUTHOR);
export const setLibraryImportValue = createAction(SET_LIBRARY_IMPORT_VALUE);
export const setLibraryImportDefault = createAction(SET_LIBRARY_IMPORT_DEFAULT);
export const importAuthors = createThunk(IMPORT_AUTHORS);
export const clearLibraryImport = createAction(CLEAR_LIBRARY_IMPORT);

//
// Helpers

// Every row update below is updateOnly. The base reducer appends when the id
// is missing, and a lookup routinely outlives its row — the row is imported
// while its search is still in flight — so without the flag the late result
// re-adds the row as a ghost with no folder behind it.
function lookupAuthor(dispatch, item) {
  dispatch(updateItem({
    section,
    updateOnly: true,
    id: item.id,
    isFetching: true,
    error: null
  }));

  const { request, abortRequest } = createAjaxRequest({
    url: '/author/lookup',
    data: {
      term: item.term
    }
  });

  abortCurrentLookups.push(abortRequest);

  request.done((data) => {
    dispatch(updateItem({
      section,
      updateOnly: true,
      id: item.id,
      isFetching: false,
      isPopulated: true,
      error: null,
      items: data,

      // Pre-select the best match so a tidy library can be imported without
      // touching every row, but leave it selectable — OpenLibrary returns
      // plenty of near-namesakes and the top hit is not always the right one.
      selectedAuthor: data[0]
    }));
  });

  request.fail((xhr) => {
    if (xhr.aborted) {
      return;
    }

    dispatch(updateItem({
      section,
      updateOnly: true,
      id: item.id,
      isFetching: false,
      isPopulated: false,
      error: xhr
    }));
  });

  request.always(() => {
    abortCurrentLookups = abortCurrentLookups.filter((abort) => abort !== abortRequest);
  });

  return request;
}

// Draining happens here rather than inside lookupAuthor so the two functions
// don't have to reference each other. Re-entry is via an async callback, so
// this recurses without growing the stack.
function processQueue(dispatch) {
  if (inFlight >= CONCURRENT_LOOKUPS || !queue.length) {
    return;
  }

  const item = queue.shift();
  const gen = generation;

  inFlight++;

  lookupAuthor(dispatch, item).always(() => {
    if (gen !== generation) {
      return;
    }

    inFlight--;
    processQueue(dispatch);
  });

  // Fill any remaining concurrency slots. Each call either returns early or
  // shortens the queue, so this terminates.
  processQueue(dispatch);
}

//
// Action Handlers

export const actionHandlers = handleThunks({

  [QUEUE_LOOKUP_AUTHOR]: function(getState, payload, dispatch) {
    const { id, term } = payload;

    // A re-queue of a row already waiting replaces the pending term rather
    // than searching twice — otherwise typing in the box stacks up a request
    // per keystroke behind the queue.
    const queued = queue.find((item) => item.id === id);

    if (queued) {
      queued.term = term;
    } else {
      queue.push({ id, term });
    }

    // Searching again drops the current match, and with it the row's tick
    // (LibraryImport only keeps a tick on a row that has a selectedAuthor).
    // Otherwise the new first hit would slide in under a tick the user gave
    // to a different author, and Import would add one they never looked at.
    dispatch(updateItem({
      section,
      updateOnly: true,
      id,
      term,
      isFetching: true,
      selectedAuthor: undefined,
      importOutcome: null
    }));

    processQueue(dispatch);
  },

  [IMPORT_AUTHORS]: function(getState, payload, dispatch) {
    // Reset skippedCount here, not just on success — otherwise a warning from
    // a previous attempt outlives the retry that fixed it.
    dispatch(set({ section, isImporting: true, importError: null, skippedCount: 0 }));

    const state = getState()[section];
    const ids = payload.ids;

    const authors = ids.reduce((acc, id) => {
      const item = state.items.find((x) => x.id === id);

      if (!item || !item.selectedAuthor) {
        return acc;
      }

      const author = getNewAuthor(_.cloneDeep(item.selectedAuthor), {
        ...state.defaults,
        rootFolderPath: ''
      });

      // The whole point of the wizard: bind the author to the folder that is
      // already on disk instead of letting AddAuthorService derive a fresh
      // one from the naming format and leave the existing files orphaned.
      author.path = item.path;

      acc.push(author);

      return acc;
    }, []);

    const promise = createAjaxRequest({
      url: '/author/import',
      method: 'POST',
      dataType: 'json',
      contentType: 'application/json',
      data: JSON.stringify(authors)
    }).request;

    promise.done((data) => {
      // The response is the authoritative account of what happened: `imported`
      // holds the authors that were added, `skipped` one entry per row that
      // was not, with the reason. Rows that made it drop off the table; the
      // rest stay, each labelled with why, which beats navigating away as
      // though everything succeeded.
      const imported = data.imported || [];
      const skipped = data.skipped || [];
      const paths = imported.map((author) => author.path);
      const importedPaths = new Set(paths);

      // A lookup still waiting its turn for a row that has just been imported
      // would spend a metered search on a row that no longer exists.
      for (let i = queue.length - 1; i >= 0; i--) {
        if (importedPaths.has(queue[i].id)) {
          queue.splice(i, 1);
        }
      }

      const actions = [
        ...imported.map((author) => updateItem({ section: 'authors', ...author })),

        set({
          section,
          isImporting: false,
          importError: null
        }),

        removeImportedFolders({ paths, skipped })
      ];

      // The rows are seeded from the root folder's unmappedFolders, so the
      // imported ones have to come out of that list too, or anything reading
      // it before the next fetch still counts them as unmapped.
      const rootFolder = getState().settings.rootFolders.items
        .find((folder) => folder.id === state.rootFolderId);

      if (rootFolder && rootFolder.unmappedFolders) {
        actions.push(updateItem({
          section: 'settings.rootFolders',
          updateOnly: true,
          id: rootFolder.id,
          unmappedFolders: rootFolder.unmappedFolders
            .filter((folder) => !importedPaths.has(folder.path))
        }));
      }

      dispatch(batchActions(actions));
    });

    promise.fail((xhr) => {
      dispatch(set({
        section,
        isImporting: false,
        importError: xhr
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [SET_LIBRARY_IMPORT_FOLDERS]: function(state, { payload }) {
    const newState = getSectionState(state, section);
    const { rootFolderId, unmappedFolders } = payload;

    newState.rootFolderId = rootFolderId;
    newState.isPopulated = true;
    newState.itemMap = unmappedFolders.reduce((acc, folder, index) => {
      acc[folder.path] = index;
      return acc;
    }, {});
    newState.items = unmappedFolders.map((folder) => ({
      id: folder.path,
      path: folder.path,
      name: folder.name,
      term: folder.name,
      isFetching: false,
      isPopulated: false,
      error: null,
      items: [],
      selectedAuthor: undefined,
      importOutcome: null
    }));

    return updateSectionState(state, section, newState);
  },

  [REMOVE_IMPORTED_FOLDERS]: function(state, { payload }) {
    const newState = getSectionState(state, section);
    const { paths, skipped = [] } = payload;
    const imported = new Set(paths);

    // Keyed by folder path, which the server echoes back untouched and which
    // is also the row id. Rebuilt from scratch so an outcome from a previous
    // attempt never outlives the attempt that replaced it.
    const outcomes = skipped.reduce((acc, { path, reason, message }) => {
      acc[path] = { reason, message };
      return acc;
    }, {});

    newState.items = newState.items
      .filter((item) => !imported.has(item.path))
      .map((item) => {
        const importOutcome = outcomes[item.path] || null;

        return item.importOutcome || importOutcome ? { ...item, importOutcome } : item;
      });
    newState.itemMap = newState.items.reduce((acc, item, index) => {
      acc[item.id] = index;
      return acc;
    }, {});
    newState.skippedCount = skipped.length;

    return updateSectionState(state, section, newState);
  },

  [SET_LIBRARY_IMPORT_VALUE]: function(state, { payload }) {
    const newState = getSectionState(state, section);
    const { id, ...other } = payload;

    // Picking a different author answers whatever the last import said about
    // the old one.
    if ('selectedAuthor' in other) {
      other.importOutcome = null;
    }

    newState.items = newState.items.map((item) => {
      return item.id === id ? { ...item, ...other } : item;
    });

    return updateSectionState(state, section, newState);
  },

  [SET_LIBRARY_IMPORT_DEFAULT]: function(state, { payload }) {
    const newState = getSectionState(state, section);

    newState.defaults = {
      ...newState.defaults,
      ...payload
    };

    return updateSectionState(state, section, newState);
  },

  [CLEAR_LIBRARY_IMPORT]: function(state) {
    clearQueue();

    const newState = getSectionState(state, section);
    const { defaults, ...rest } = defaultState;

    return updateSectionState(state, section, {
      ...newState,
      ...rest
    });
  }

}, defaultState, section);
