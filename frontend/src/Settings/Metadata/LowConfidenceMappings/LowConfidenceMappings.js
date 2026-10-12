import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { kinds } from 'Helpers/Props';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './LowConfidenceMappings.css';

// Phase 9c. The review panel called out in MetadataSwitchWizard's
// "done" state. Surfaces BookIdMapping rows below the configured
// confidence threshold; the user can paste the right OL ID and
// flip the row to Source=Manual so the reidentify passes leave it
// alone next time.
//
// A row is either a book mapping (corrected with a work ID, plus an
// optional edition) or an author mapping (corrected with an author
// ID). The server says which in `kind` and refuses a save of the wrong
// shape, so each kind gets its own field here.
//
// This panel does not use the Redux store — there's no shared state
// to coordinate, and the data is loaded on-demand. A direct ajax
// call keeps it self-contained, consistent with the Phase 5 wizard
// scope.

const ENDPOINT = '/metadata/lowconfidencemapping';
const KIND_AUTHOR = 'author';

function confidenceClass(value) {
  if (value < 0.5) {
    return styles.confidenceLow;
  }

  return styles.confidenceMid;
}

// A rejected save comes back as FluentValidation's failure list; anything
// else that carries a body uses the API's { message } error model.
function getErrorMessage(xhr, fallback) {
  const json = xhr && xhr.responseJSON;

  if (Array.isArray(json)) {
    const messages = json.map((failure) => failure && failure.errorMessage).filter(Boolean);

    if (messages.length) {
      return messages.join(' ');
    }
  }

  if (json && typeof json.message === 'string' && json.message) {
    return json.message;
  }

  return fallback;
}

function editedValue(edit, field, stored) {
  return edit[field] === undefined ? (stored || '') : edit[field];
}

class LowConfidenceMappings extends Component {

  constructor(props, context) {
    super(props, context);

    this._isMounted = false;

    this.state = {
      isLoading: true,
      loadError: null,
      mappings: [],
      // Per-row in-flight edits, keyed by mapping ID.
      // {olWorkId, olEditionId, olAuthorId}
      edits: {},
      // Per-row save failures, keyed by mapping ID. A failed save belongs
      // to its row: it must not take the table, or what the user has typed
      // into the other rows, down with it.
      rowErrors: {},
      savingId: null
    };
  }

  componentDidMount() {
    this._isMounted = true;
    this.loadMappings();
  }

  componentDidUpdate(prevProps) {
    // The wizard bumps this when a reidentify run ends, which is exactly
    // when the list this panel loaded on mount stops being true.
    if (prevProps.reloadToken !== this.props.reloadToken) {
      this.loadMappings();
    }
  }

  componentWillUnmount() {
    this._isMounted = false;
  }

  loadMappings = () => {
    this.setState({ isLoading: true, loadError: null });

    createAjaxRequest({
      url: ENDPOINT,
      method: 'GET',
      dataType: 'json'
    }).request.done((data) => {
      if (!this._isMounted) {
        return;
      }

      this.setState({
        isLoading: false,
        mappings: data
      });
    }).fail((xhr) => {
      if (xhr.aborted || !this._isMounted) {
        return;
      }

      this.setState({
        isLoading: false,
        loadError: getErrorMessage(xhr, 'Failed to load mappings')
      });
    });
  };

  setRowError = (id, message) => {
    this.setState((prev) => ({
      rowErrors: {
        ...prev.rowErrors,
        [id]: message
      }
    }));
  };

  onRetryPress = () => {
    this.loadMappings();
  };

  onFieldChange = (id, field, value) => {
    this.setState((prev) => {
      const { [id]: cleared, ...rowErrors } = prev.rowErrors;

      return {
        // Typing is the user's answer to the error; stop showing it.
        rowErrors,
        edits: {
          ...prev.edits,
          [id]: {
            ...(prev.edits[id] || {}),
            [field]: value
          }
        }
      };
    });
  };

  onSaveRow = (mapping) => {
    const edit = this.state.edits[mapping.id] || {};
    const isAuthor = mapping.kind === KIND_AUTHOR;

    // Each kind sends only its own ids, with the other kind's explicitly
    // nulled: the server rejects a work ID on an author row (and vice
    // versa) rather than guessing which one was meant.
    const ids = isAuthor ?
      {
        openLibraryWorkId: null,
        openLibraryEditionId: null,
        openLibraryAuthorId: editedValue(edit, 'olAuthorId', mapping.openLibraryAuthorId).trim()
      } :
      {
        openLibraryWorkId: editedValue(edit, 'olWorkId', mapping.openLibraryWorkId).trim(),
        openLibraryEditionId: editedValue(edit, 'olEditionId', mapping.openLibraryEditionId).trim() || null,
        openLibraryAuthorId: null
      };

    if (isAuthor && !ids.openLibraryAuthorId) {
      this.setRowError(mapping.id, 'Enter the author\'s Open Library ID (OLxxxxA) before saving.');
      return;
    }

    if (!isAuthor && !ids.openLibraryWorkId) {
      this.setRowError(mapping.id, 'Enter the book\'s Open Library work ID (OLxxxxW) before saving.');
      return;
    }

    this.setState({ savingId: mapping.id });

    createAjaxRequest({
      url: `${ENDPOINT}/${mapping.id}`,
      method: 'PUT',
      dataType: 'json',
      data: JSON.stringify({ ...mapping, ...ids })
    }).request.done(() => {
      if (!this._isMounted) {
        return;
      }

      // Manual rows drop below the low-confidence threshold (they're
      // now 1.0), so the cleanest UX is to refetch — the saved row
      // simply disappears from the list. Edits to the other rows are
      // keyed by ID and survive the refetch.
      this.setState((prev) => {
        const { [mapping.id]: savedEdit, ...edits } = prev.edits;
        const { [mapping.id]: savedError, ...rowErrors } = prev.rowErrors;

        return { savingId: null, edits, rowErrors };
      }, this.loadMappings);
    }).fail((xhr) => {
      if (!this._isMounted) {
        return;
      }

      this.setState({ savingId: null });
      this.setRowError(mapping.id, getErrorMessage(xhr, 'Save failed. Check the ID and try again.'));
    });
  };

  renderRow(m) {
    const {
      edits,
      rowErrors,
      savingId
    } = this.state;

    const edit = edits[m.id] || {};
    const isAuthor = m.kind === KIND_AUTHOR;
    const rowError = rowErrors[m.id];
    const currentId = isAuthor ? m.openLibraryAuthorId : m.openLibraryWorkId;

    return (
      <tr key={m.id}>
        <td>
          {isAuthor ?
            <div>{m.authorName || `(unknown author ${m.goodreadsId})`}</div> :
            <div>{m.bookTitle || `(unknown book ${m.goodreadsId})`}</div>
          }
          {!isAuthor && m.authorName && <small>{m.authorName}</small>}
        </td>
        <td>{isAuthor ? 'Author' : 'Book'}</td>
        <td>{m.source}</td>
        <td className={confidenceClass(m.confidence)}>
          {m.confidence.toFixed(2)}
        </td>
        <td><code>{currentId || '—'}</code></td>
        {isAuthor ?
          <td colSpan={2}>
            <input
              className={styles.olInput}
              type="text"
              aria-label="Override OL author ID"
              value={editedValue(edit, 'olAuthorId', m.openLibraryAuthorId)}
              placeholder="OLxxxxxxA"
              onChange={(e) => this.onFieldChange(m.id, 'olAuthorId', e.target.value)}
            />
            {rowError && <div className={styles.rowError} role="alert">{rowError}</div>}
          </td> :
          <>
            <td>
              <input
                className={styles.olInput}
                type="text"
                aria-label="Override OL work ID"
                value={editedValue(edit, 'olWorkId', m.openLibraryWorkId)}
                placeholder="OLxxxxxxW"
                onChange={(e) => this.onFieldChange(m.id, 'olWorkId', e.target.value)}
              />
              {rowError && <div className={styles.rowError} role="alert">{rowError}</div>}
            </td>
            <td>
              <input
                className={styles.olInput}
                type="text"
                aria-label="Override OL edition ID"
                value={editedValue(edit, 'olEditionId', m.openLibraryEditionId)}
                placeholder="OLxxxxxxM"
                onChange={(e) => this.onFieldChange(m.id, 'olEditionId', e.target.value)}
              />
            </td>
          </>
        }
        <td className={styles.actionCell}>
          <Button
            kind={kinds.PRIMARY}
            onPress={() => this.onSaveRow(m)}
            isDisabled={savingId !== null}
          >
            Save as Manual
          </Button>
        </td>
      </tr>
    );
  }

  render() {
    const {
      isLoading,
      loadError,
      mappings
    } = this.state;

    return (
      <FieldSet legend="Low-confidence metadata mappings">
        <div className={styles.body}>
          <p>
            Books and authors whose Goodreads → Open Library mapping
            confidence fell below 0.70. For a book, enter the correct
            Open Library work ID (<code>OLxxxxW</code>) and optionally an
            edition (<code>OLxxxxM</code>); for an author, enter the
            author ID (<code>OLxxxxA</code>). Pasting the
            openlibrary.org link, or a key such as <code>/works/OLxxxxW</code> or <code>/authors/OLxxxxA</code>,
            works too. Saving records the row as a Manual override and
            future reidentify passes will leave it alone.
          </p>

          {isLoading && <LoadingIndicator />}

          {!isLoading && loadError && (
            <div className={styles.loadError} role="alert">
              <span className={styles.loadErrorMessage}>{loadError}</span>
              <Button onPress={this.onRetryPress}>
                Retry
              </Button>
            </div>
          )}

          {!isLoading && !loadError && mappings.length === 0 && (
            <p className={styles.empty}>
              No low-confidence mappings — everything matched above the threshold,
              or the reidentify pass has not yet run.
            </p>
          )}

          {/* Deliberately not gated on isLoading or loadError: a refresh
              (after a save, or when a reidentify run ends) must not unmount
              the inputs the user is part-way through filling in. */}
          {mappings.length > 0 && (
            <table className={styles.table}>
              <thead>
                <tr>
                  <th>Book / Author</th>
                  <th>Type</th>
                  <th>Source</th>
                  <th>Confidence</th>
                  <th>Current OL ID</th>
                  <th>Override OL Work / Author ID</th>
                  <th>Override OL Edition</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {mappings.map((m) => this.renderRow(m))}
              </tbody>
            </table>
          )}
        </div>
      </FieldSet>
    );
  }
}

LowConfidenceMappings.propTypes = {
  reloadToken: PropTypes.number
};

export default LowConfidenceMappings;
