import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import { kinds } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';
import areAllSelected from 'Utilities/Table/areAllSelected';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import LibraryImportFooter from './LibraryImportFooter';
import LibraryImportRow from './LibraryImportRow';
import styles from './LibraryImport.css';

const columns = [
  {
    name: 'folder',
    label: () => translate('Folder'),
    isVisible: true
  },
  {
    name: 'term',
    label: () => translate('LibraryImportSearchTerm'),
    isVisible: true
  },
  {
    name: 'author',
    label: () => translate('Author'),
    isVisible: true
  }
];

class LibraryImport extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      allSelected: false,
      allUnselected: false,
      lastToggled: null,
      selectedState: {}
    };
  }

  componentDidMount() {
    this.props.onMount();
  }

  componentDidUpdate(prevProps) {
    // Rows become selectable only once their lookup resolves, so the header's
    // select-all state has to be recomputed as results stream in.
    //
    // Nothing is ever selected automatically. Importing creates authors and
    // binds them to folders on disk; pre-ticking a few hundred rows the user
    // has not looked at would make the destructive default the easy one.
    if (prevProps.items !== this.props.items) {
      this.setState((state) => {
        const selectedState = this.props.items.reduce((acc, item) => {
          acc[item.id] = item.selectedAuthor ? (state.selectedState[item.id] ?? false) : false;

          return acc;
        }, {});

        return {
          ...areAllSelected(selectedState),
          selectedState
        };
      });
    }
  }

  componentWillUnmount() {
    this.props.onUnmount();
  }

  //
  // Control

  // Select-all ticks every row including ones still waiting on a lookup, so
  // filter down to rows that actually have an author to import. Without this
  // the footer count promises more than the import can deliver.
  getSelectedIds = () => {
    const importable = new Set(
      this.props.items.filter((item) => item.selectedAuthor).map((item) => item.id)
    );

    return getSelectedIds(this.state.selectedState, { parseIds: false })
      .filter((id) => importable.has(id));
  };

  //
  // Listeners

  onSelectAllChange = ({ value }) => {
    this.setState(selectAll(this.state.selectedState, value));
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    this.setState((state) => {
      return toggleSelected(state, this.props.items, id, value, shiftKey);
    });
  };

  onImportPress = () => {
    this.props.onImportPress(this.getSelectedIds());
  };

  //
  // Render

  render() {
    const {
      rootFolderPath,
      isPopulated,
      error,
      rootFolderNotFound,
      items,
      isImporting,
      importError,
      skippedCount,
      defaults,
      onQueueLookup,
      onSetValue,
      onInputChange
    } = this.props;

    const {
      allSelected,
      allUnselected,
      selectedState
    } = this.state;

    const selectedCount = this.getSelectedIds().length;
    const hasError = !!error || rootFolderNotFound;

    // Whatever the server said went wrong, when it said anything.
    const importErrorMessage = getErrorMessage(importError, null);

    return (
      <PageContent title={translate('LibraryImport')}>
        <PageContentBody>
          {
            !isPopulated && !hasError &&
              <LoadingIndicator />
          }

          {
            !!error &&
              <Alert kind={kinds.DANGER}>
                {translate('LibraryImportUnableToLoad')}
              </Alert>
          }

          {
            !error && rootFolderNotFound &&
              <Alert kind={kinds.DANGER}>
                {translate('LibraryImportRootFolderNotFound')}
              </Alert>
          }

          {
            !!importError &&
              <Alert kind={kinds.DANGER}>
                <div>{translate('LibraryImportFailed')}</div>
                {
                  !!importErrorMessage &&
                    <div>{importErrorMessage}</div>
                }
              </Alert>
          }

          {
            !importError && !!skippedCount &&
              <Alert kind={kinds.WARNING}>
                {translate('LibraryImportPartialFailure', { count: skippedCount })}
              </Alert>
          }

          {
            isPopulated && !hasError && !items.length &&
              <Alert kind={kinds.INFO}>
                {translate('LibraryImportNoUnmappedFolders', { path: rootFolderPath })}
              </Alert>
          }

          {
            isPopulated && !hasError && !!items.length &&
              <div>
                <div className={styles.description}>
                  {translate('LibraryImportTableDescription')}
                </div>

                <Table
                  columns={columns}
                  selectAll={true}
                  allSelected={allSelected}
                  allUnselected={allUnselected}
                  onSelectAllChange={this.onSelectAllChange}
                >
                  <TableBody>
                    {
                      items.map((item) => {
                        return (
                          <LibraryImportRow
                            key={item.id}
                            {...item}
                            isSelected={selectedState[item.id] ?? false}
                            onSelectedChange={this.onSelectedChange}
                            onQueueLookup={onQueueLookup}
                            onSetValue={onSetValue}
                          />
                        );
                      })
                    }
                  </TableBody>
                </Table>
              </div>
          }
        </PageContentBody>

        {
          isPopulated && !hasError && !!items.length &&
            <LibraryImportFooter
              selectedCount={selectedCount}
              isImporting={isImporting}
              {...defaults}
              onInputChange={onInputChange}
              onImportPress={this.onImportPress}
            />
        }
      </PageContent>
    );
  }
}

LibraryImport.propTypes = {
  rootFolderPath: PropTypes.string.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  rootFolderNotFound: PropTypes.bool.isRequired,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  isImporting: PropTypes.bool.isRequired,
  importError: PropTypes.object,
  skippedCount: PropTypes.number.isRequired,
  defaults: PropTypes.object.isRequired,
  onMount: PropTypes.func.isRequired,
  onUnmount: PropTypes.func.isRequired,
  onQueueLookup: PropTypes.func.isRequired,
  onSetValue: PropTypes.func.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onImportPress: PropTypes.func.isRequired
};

export default LibraryImport;
