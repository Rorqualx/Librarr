import { act, fireEvent, render, screen, within } from '@testing-library/react';
import $ from 'jquery';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import LowConfidenceMappings from './LowConfidenceMappings';

// The panel talks to the API directly, so the request layer is the seam.
// Each call hands back a jQuery Deferred the test settles by hand, which is
// the same object shape (done/fail, synchronous callbacks) the real one has.
vi.mock('Utilities/createAjaxRequest', () => ({ default: vi.fn() }));

const BOOK = {
  id: 1,
  kind: 'book',
  goodreadsId: '6693',
  openLibraryWorkId: 'OL200W',
  openLibraryEditionId: 'OL300M',
  openLibraryAuthorId: null,
  confidence: 0.55,
  source: 'TitleAuthor',
  bookId: 42,
  bookTitle: 'Fantastic Mr Fox',
  authorName: 'Roald Dahl'
};

const AUTHOR = {
  id: 2,
  kind: 'author',
  goodreadsId: '4273',
  openLibraryWorkId: null,
  openLibraryEditionId: null,
  openLibraryAuthorId: 'OL498120A',
  confidence: 0.5,
  source: 'TitleAuthor',
  bookId: null,
  bookTitle: null,
  authorName: 'Roald Dahl'
};

let requests = [];

function lastRequest(method) {
  return requests.filter((r) => r.options.method === method).pop();
}

// Settling a request runs the component's done/fail callback synchronously,
// which sets state -- so it has to happen inside act like any other update.
function resolve(request, value) {
  act(() => {
    request.deferred.resolve(value);
  });
}

function reject(request, xhr) {
  act(() => {
    request.deferred.reject(xhr);
  });
}

function rowFor(text) {
  return screen.getByText(text).closest('tr');
}

function renderLoaded(mappings = [BOOK, AUTHOR], props = {}) {
  const view = render(<LowConfidenceMappings {...props} />);

  resolve(lastRequest('GET'), mappings);

  return view;
}

function validationFailure(errorMessage) {
  return { status: 400, responseJSON: [{ propertyName: 'openLibraryWorkId', errorMessage }] };
}

beforeEach(() => {
  requests = [];

  createAjaxRequest.mockReset();
  createAjaxRequest.mockImplementation((options) => {
    const deferred = $.Deferred();

    requests.push({ options, deferred });

    return { request: deferred.promise(), abortRequest: vi.fn() };
  });
});

describe('LowConfidenceMappings', () => {
  it('presents an author row as an author, with an author field and no book fields', () => {
    renderLoaded([AUTHOR]);

    const row = within(rowFor('OL498120A'));

    expect(row.getByText('Roald Dahl')).toBeInTheDocument();
    expect(row.getByText('Author')).toBeInTheDocument();
    expect(row.getByLabelText('Override OL author ID')).toHaveValue('OL498120A');
    expect(row.queryByLabelText('Override OL work ID')).not.toBeInTheDocument();
    expect(row.queryByLabelText('Override OL edition ID')).not.toBeInTheDocument();
    expect(screen.queryByText(/unknown book/)).not.toBeInTheDocument();
  });

  it('names an author row it cannot match locally as an unknown author', () => {
    renderLoaded([{ ...AUTHOR, authorName: null }]);

    expect(screen.getByText('(unknown author 4273)')).toBeInTheDocument();
  });

  it('saves an author row as an author id and never as a work id', () => {
    renderLoaded([AUTHOR]);

    const row = within(rowFor('OL498120A'));

    fireEvent.change(row.getByLabelText('Override OL author ID'), { target: { value: ' /authors/OL34184A ' } });
    fireEvent.click(row.getByText('Save as Manual'));

    const put = lastRequest('PUT');
    const body = JSON.parse(put.options.data);

    expect(put.options.url).toBe('/metadata/lowconfidencemapping/2');
    expect(body.openLibraryAuthorId).toBe('/authors/OL34184A');
    expect(body.openLibraryWorkId).toBeNull();
    expect(body.openLibraryEditionId).toBeNull();
  });

  it('saves a book row as a work id and optional edition', () => {
    renderLoaded([BOOK]);

    const row = within(rowFor('Fantastic Mr Fox'));

    fireEvent.change(row.getByLabelText('Override OL work ID'), { target: { value: '/works/OL45804W' } });
    fireEvent.change(row.getByLabelText('Override OL edition ID'), { target: { value: '' } });
    fireEvent.click(row.getByText('Save as Manual'));

    const body = JSON.parse(lastRequest('PUT').options.data);

    expect(body.openLibraryWorkId).toBe('/works/OL45804W');
    expect(body.openLibraryEditionId).toBeNull();
    expect(body.openLibraryAuthorId).toBeNull();
  });

  it('explains an empty id against the row instead of doing nothing', () => {
    renderLoaded([AUTHOR]);

    const row = within(rowFor('OL498120A'));

    fireEvent.change(row.getByLabelText('Override OL author ID'), { target: { value: '   ' } });
    fireEvent.click(row.getByText('Save as Manual'));

    expect(lastRequest('PUT')).toBeUndefined();
    expect(row.getByRole('alert')).toHaveTextContent('OLxxxxA');
  });

  // The defect: one rejected save replaced the whole table with the words
  // "Save failed" until the page was reloaded.
  it('keeps the table and every typed override when a save is rejected', () => {
    renderLoaded();

    const book = within(rowFor('Fantastic Mr Fox'));
    const author = within(rowFor('OL498120A'));

    fireEvent.change(author.getByLabelText('Override OL author ID'), { target: { value: 'OL34184A' } });
    fireEvent.change(book.getByLabelText('Override OL work ID'), { target: { value: 'nonsense' } });
    fireEvent.click(book.getByText('Save as Manual'));

    reject(lastRequest('PUT'), validationFailure('Enter an Open Library work ID such as OL45804W.'));

    expect(book.getByRole('alert')).toHaveTextContent('Enter an Open Library work ID such as OL45804W.');
    expect(book.getByLabelText('Override OL work ID')).toHaveValue('nonsense');
    expect(author.getByLabelText('Override OL author ID')).toHaveValue('OL34184A');
    expect(author.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('lets a rejected row be corrected and saved again', () => {
    renderLoaded([BOOK]);

    const row = within(rowFor('Fantastic Mr Fox'));
    const input = row.getByLabelText('Override OL work ID');

    fireEvent.change(input, { target: { value: 'nonsense' } });
    fireEvent.click(row.getByText('Save as Manual'));
    reject(lastRequest('PUT'), validationFailure('Not a work ID.'));

    fireEvent.change(input, { target: { value: 'OL45804W' } });

    expect(row.queryByRole('alert')).not.toBeInTheDocument();

    fireEvent.click(row.getByText('Save as Manual'));

    expect(requests.filter((r) => r.options.method === 'PUT')).toHaveLength(2);
    expect(JSON.parse(lastRequest('PUT').options.data).openLibraryWorkId).toBe('OL45804W');
  });

  it('falls back to a generic message when a failed save carries no detail', () => {
    renderLoaded([BOOK]);

    const row = within(rowFor('Fantastic Mr Fox'));

    fireEvent.click(row.getByText('Save as Manual'));
    reject(lastRequest('PUT'), { status: 500 });

    expect(row.getByRole('alert')).toHaveTextContent('Save failed');
  });

  it('reloads after a successful save and keeps edits made to other rows', () => {
    renderLoaded();

    const book = within(rowFor('Fantastic Mr Fox'));

    fireEvent.change(within(rowFor('OL498120A')).getByLabelText('Override OL author ID'), { target: { value: 'OL34184A' } });
    fireEvent.click(book.getByText('Save as Manual'));
    resolve(lastRequest('PUT'), { ...BOOK, source: 'Manual', confidence: 1 });

    expect(requests.filter((r) => r.options.method === 'GET')).toHaveLength(2);

    resolve(lastRequest('GET'), [AUTHOR]);

    expect(screen.queryByText('Fantastic Mr Fox')).not.toBeInTheDocument();
    expect(within(rowFor('OL498120A')).getByLabelText('Override OL author ID')).toHaveValue('OL34184A');
  });

  it('reloads when the reload token changes', () => {
    const { rerender } = renderLoaded([BOOK], { reloadToken: 0 });

    rerender(<LowConfidenceMappings reloadToken={0} />);
    expect(requests.filter((r) => r.options.method === 'GET')).toHaveLength(1);

    rerender(<LowConfidenceMappings reloadToken={1} />);
    expect(requests.filter((r) => r.options.method === 'GET')).toHaveLength(2);

    resolve(lastRequest('GET'), [BOOK, AUTHOR]);

    expect(screen.getByText('OL498120A')).toBeInTheDocument();
  });

  it('offers a retry when the list cannot be loaded', () => {
    render(<LowConfidenceMappings />);

    reject(lastRequest('GET'), { status: 500 });

    expect(screen.getByRole('alert')).toHaveTextContent('Failed to load mappings');

    fireEvent.click(screen.getByText('Retry'));
    resolve(lastRequest('GET'), [BOOK]);

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText('Fantastic Mr Fox')).toBeInTheDocument();
  });
});
