import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FormInputGroup from 'Components/Form/FormInputGroup';
import Icon from 'Components/Icon';
import SpinnerIcon from 'Components/SpinnerIcon';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import TableSelectCell from 'Components/Table/Cells/TableSelectCell';
import TableRow from 'Components/Table/TableRow';
import { icons, inputTypes, kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './LibraryImportRow.css';

// The only thing that reliably separates two same-named Open Library
// records. OL carries three "Stephen King" author records — 606 works
// (Carrie), 48 (Misery), and 7 (Principles of Macroeconomics) — and a
// zero-work record is almost always an empty stub rather than a real
// alternate. Shown for every row, not just collisions: on a single
// result it's the difference between "matched the real author" and
// "matched a stub", which is the question this page exists to answer.
function describeMatch({ workCount, topWork }) {
  if (typeof workCount !== 'number') {
    return null;
  }

  if (workCount === 0) {
    return translate('LibraryImportNoWorks');
  }

  // AuthorWorkCount, shared with the Add Author search results rather than
  // a second key holding the same English string.
  const works = translate('AuthorWorkCount', { count: workCount });

  return topWork ? `${works}, ${topWork}` : works;
}

// Why the last import left this row behind. The two reasons the server names
// get their own wording; anything else is a lookup or validation failure and
// the server's message is the most specific thing there is to say about it.
function describeOutcome({ reason, message }) {
  if (reason === 'duplicate') {
    return translate('LibraryImportSkippedDuplicate');
  }

  if (reason === 'exists') {
    return translate('LibraryImportSkippedExists');
  }

  return message || translate('LibraryImportSkippedFailed');
}

class LibraryImportRow extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      id,
      term,
      isPopulated,
      isFetching
    } = this.props;

    if (!isPopulated && !isFetching) {
      this.props.onQueueLookup({ id, term });
    }
  }

  //
  // Listeners

  onTermChange = ({ value }) => {
    const { id } = this.props;

    this.props.onSetValue({ id, term: value });
  };

  onSearchPress = () => {
    const { id, term } = this.props;

    this.props.onQueueLookup({ id, term });
  };

  onAuthorSelect = ({ value }) => {
    const { id, items } = this.props;

    this.props.onSetValue({
      id,
      selectedAuthor: items.find((author) => author.foreignAuthorId === value)
    });
  };

  //
  // Render

  render() {
    const {
      id,
      name,
      term,
      isFetching,
      isPopulated,
      error,
      items,
      selectedAuthor,
      importOutcome,
      isSelected,
      onSelectedChange
    } = this.props;

    // OpenLibrary routinely returns several records for the same author, and
    // none of them carry a disambiguation — a search for "Brandon Sanderson"
    // comes back as two records identical in every field the API exposes
    // except the OL id. describeMatch() uses the work count OL does send;
    // the OL id stays as the fallback for a response that predates it, so
    // the dropdown never offers choices the user can't tell apart.
    const nameCounts = items.reduce((acc, author) => {
      acc[author.authorName] = (acc[author.authorName] || 0) + 1;

      return acc;
    }, {});

    const authorOptions = items.map((author) => {
      const match = describeMatch(author);
      const qualifiers = [];

      if (author.disambiguation) {
        qualifiers.push(author.disambiguation);
      }

      if (match) {
        qualifiers.push(match);
      } else if (nameCounts[author.authorName] > 1) {
        // Nothing to describe the record by, so fall back to the raw id
        // rather than offer two options spelled the same way.
        qualifiers.push(author.foreignAuthorId);
      }

      return {
        key: author.foreignAuthorId,
        value: qualifiers.length ?
          `${author.authorName} — ${qualifiers.join(', ')}` :
          author.authorName
      };
    });

    return (
      <TableRow>
        <TableSelectCell
          id={id}
          isSelected={isSelected}
          isDisabled={!selectedAuthor}
          onSelectedChange={onSelectedChange}
        />

        <TableRowCell className={styles.folder}>
          {name}
        </TableRowCell>

        <TableRowCell className={styles.term}>
          <FormInputGroup
            type={inputTypes.TEXT}
            name="term"
            value={term}
            buttons={[
              <div
                key="search"
                className={styles.searchButton}
                onClick={this.onSearchPress}
                role="button"
                tabIndex={0}
                onKeyPress={this.onSearchPress}
              >
                <Icon name={icons.SEARCH} />
              </div>
            ]}
            onChange={this.onTermChange}
          />
        </TableRowCell>

        <TableRowCell className={styles.author}>
          {
            isFetching &&
              <SpinnerIcon
                name={icons.SPINNER}
                isSpinning={true}
              />
          }

          {
            !isFetching && !!error &&
              <Icon
                name={icons.WARNING}
                kind={kinds.WARNING}
                title={translate('LibraryImportLookupFailed')}
              />
          }

          {
            !isFetching && !error && isPopulated && !items.length &&
              <span className={styles.noMatches}>
                {translate('LibraryImportNoMatches')}
              </span>
          }

          {
            !isFetching && !error && !!items.length &&
              <FormInputGroup
                type={inputTypes.SELECT}
                name="selectedAuthor"
                value={selectedAuthor ? selectedAuthor.foreignAuthorId : ''}
                values={authorOptions}
                onChange={this.onAuthorSelect}
              />
          }

          {
            !isFetching && !!importOutcome &&
              <div className={styles.outcome}>
                <Icon
                  className={styles.outcomeIcon}
                  name={icons.WARNING}
                  kind={kinds.WARNING}
                />

                {describeOutcome(importOutcome)}
              </div>
          }
        </TableRowCell>
      </TableRow>
    );
  }
}

LibraryImportRow.propTypes = {
  id: PropTypes.string.isRequired,
  name: PropTypes.string.isRequired,
  term: PropTypes.string.isRequired,
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  selectedAuthor: PropTypes.object,
  importOutcome: PropTypes.object,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired,
  onQueueLookup: PropTypes.func.isRequired,
  onSetValue: PropTypes.func.isRequired
};

export default LibraryImportRow;
