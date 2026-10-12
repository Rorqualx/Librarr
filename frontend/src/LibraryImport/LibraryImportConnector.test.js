import { act, fireEvent, render, screen } from '@testing-library/react';
import $ from 'jquery';
import React from 'react';
import { Provider } from 'react-redux';
import { applyMiddleware, combineReducers, createStore } from 'redux';
import { enableBatching } from 'redux-batched-actions';
import thunk from 'redux-thunk';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import createHandleActions from 'Store/Actions/Creators/createHandleActions';
import { reducers as libraryImport } from 'Store/Actions/libraryImportActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import LibraryImportConnector from './LibraryImportConnector';

// Requests are Deferreds settled by hand: what this page shows depends on
// which fetch has landed, and that is exactly what went wrong before.
vi.mock('Utilities/createAjaxRequest', () => ({ default: vi.fn() }));

// The footer drags in the profile and tag pickers, each wanting its own slice
// of the store. Nothing here is about them.
vi.mock('./LibraryImportFooter', () => ({
  default: ({ selectedCount, onImportPress }) => (
    <button data-testid="footer" onClick={onImportPress}>{selectedCount}</button>
  )
}));

const HARPER = { path: '/books/Harper Lee', name: 'Harper Lee' };
const ORWELL = { path: '/books/George Orwell', name: 'George Orwell' };

const AUTHOR = { foreignAuthorId: 'OL1A', authorName: 'Harper Lee' };
const OTHER = { foreignAuthorId: 'OL2A', authorName: 'Someone Else' };

let requests = [];

function pending(url) {
  return requests.filter((r) => r.options.url === url && r.deferred.state() === 'pending');
}

function settle(url, method, value) {
  act(() => {
    pending(url)[0].deferred[method](value);
  });
}

function renderPage(rootFolderId, rootFolders = {}) {
  const store = createStore(
    enableBatching(combineReducers({
      libraryImport,
      settings: createHandleActions({}, {
        rootFolders: {
          isFetching: false,
          isPopulated: false,
          error: null,
          items: [],
          ...rootFolders
        }
      }, 'settings')
    })),
    applyMiddleware(thunk)
  );

  const result = render(
    <Provider store={store}>
      <LibraryImportConnector match={{ params: { rootFolderId: `${rootFolderId}` } }} />
    </Provider>
  );

  return { ...result, store };
}

beforeEach(() => {
  requests = [];

  createAjaxRequest.mockImplementation((options) => {
    const deferred = $.Deferred();

    requests.push({ options, deferred });

    return { request: deferred.promise(), abortRequest: vi.fn() };
  });
});

describe('LibraryImportConnector', () => {
  // /add/import/999 used to sit on the loading indicator forever: the fetch
  // succeeded, the id was simply not in it, and nothing said so.
  it('says so when the root folder does not exist', () => {
    renderPage(999);

    expect(screen.queryByText('LibraryImportRootFolderNotFound')).not.toBeInTheDocument();

    settle('/rootFolder', 'resolve', [{ id: 1, path: '/books', unmappedFolders: [HARPER] }]);

    expect(screen.getByText('LibraryImportRootFolderNotFound')).toBeInTheDocument();
    expect(screen.queryByText('Harper Lee')).not.toBeInTheDocument();
  });

  it('shows the load error when the root folder fetch fails', () => {
    renderPage(1);

    settle('/rootFolder', 'reject', { status: 500 });

    expect(screen.getByText('LibraryImportUnableToLoad')).toBeInTheDocument();
    expect(screen.queryByText('LibraryImportRootFolderNotFound')).not.toBeInTheDocument();
  });

  it('lists the unmapped folders once the fetch lands', () => {
    renderPage(1);

    settle('/rootFolder', 'resolve', [{ id: 1, path: '/books', unmappedFolders: [HARPER, ORWELL] }]);

    expect(screen.getByText('Harper Lee')).toBeInTheDocument();
    expect(screen.getByText('George Orwell')).toBeInTheDocument();
  });

  // Coming back to the page after an import: the store still holds the list
  // from before, and the folders imported since must not be offered again.
  it('refetches instead of trusting a root folder list already in the store', () => {
    renderPage(1, {
      isPopulated: true,
      items: [{ id: 1, path: '/books', unmappedFolders: [HARPER, ORWELL] }]
    });

    expect(pending('/rootFolder')).toHaveLength(1);
    expect(screen.queryByText('Harper Lee')).not.toBeInTheDocument();

    settle('/rootFolder', 'resolve', [{ id: 1, path: '/books', unmappedFolders: [ORWELL] }]);

    expect(screen.queryByText('Harper Lee')).not.toBeInTheDocument();
    expect(screen.getByText('George Orwell')).toBeInTheDocument();
  });

  // The tick was given to the author on screen at the time. A new search
  // replaces that author, so the tick must not survive it.
  it('unticks a row when it is searched again', () => {
    const { container } = renderPage(1);

    settle('/rootFolder', 'resolve', [{ id: 1, path: '/books', unmappedFolders: [HARPER] }]);
    settle('/author/lookup', 'resolve', [AUTHOR]);

    const rowCheckbox = () => screen.getAllByRole('checkbox')[1];

    fireEvent.click(rowCheckbox().closest('label'));

    expect(rowCheckbox()).toBeChecked();
    expect(screen.getByTestId('footer')).toHaveTextContent('1');

    fireEvent.click(container.querySelector('.searchButton'));

    expect(rowCheckbox()).not.toBeChecked();

    settle('/author/lookup', 'resolve', [OTHER]);

    expect(rowCheckbox()).not.toBeChecked();
    expect(screen.getByTestId('footer')).toHaveTextContent('0');
  });

  it('labels each row the import skipped with the reason', () => {
    renderPage(1);

    settle('/rootFolder', 'resolve', [{ id: 1, path: '/books', unmappedFolders: [HARPER, ORWELL] }]);
    settle('/author/lookup', 'resolve', [AUTHOR]);
    settle('/author/lookup', 'resolve', [OTHER]);

    fireEvent.click(screen.getAllByRole('checkbox')[0].closest('label'));
    fireEvent.click(screen.getByTestId('footer'));

    settle('/author/import', 'resolve', {
      imported: [{ id: 10, path: HARPER.path, ...AUTHOR }],
      skipped: [{ path: ORWELL.path, foreignAuthorId: 'OL2A', reason: 'exists', message: 'already' }]
    });

    expect(screen.queryByText('Harper Lee')).not.toBeInTheDocument();
    expect(screen.getByText('George Orwell')).toBeInTheDocument();
    expect(screen.getByText('LibraryImportSkippedExists')).toBeInTheDocument();
    expect(screen.getByText('LibraryImportPartialFailure(count=1)')).toBeInTheDocument();
  });

  it('passes on what the server said when the whole import fails', () => {
    renderPage(1);

    settle('/rootFolder', 'resolve', [{ id: 1, path: '/books', unmappedFolders: [HARPER] }]);
    settle('/author/lookup', 'resolve', [AUTHOR]);

    fireEvent.click(screen.getAllByRole('checkbox')[1].closest('label'));
    fireEvent.click(screen.getByTestId('footer'));

    settle('/author/import', 'reject', { status: 400, responseJSON: { message: 'A list of authors to import is required' } });

    expect(screen.getByText('LibraryImportFailed')).toBeInTheDocument();
    expect(screen.getByText('A list of authors to import is required')).toBeInTheDocument();

    // The row is still there, as the folder name and as the selected match.
    expect(screen.getAllByText('Harper Lee')).toHaveLength(2);
  });
});
