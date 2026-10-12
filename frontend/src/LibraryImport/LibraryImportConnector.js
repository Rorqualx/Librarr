import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import {
  clearLibraryImport,
  importAuthors,
  queueLookupAuthor,
  setLibraryImportDefault,
  setLibraryImportFolders,
  setLibraryImportValue
} from 'Store/Actions/libraryImportActions';
import { fetchRootFolders } from 'Store/Actions/settingsActions';
import LibraryImport from './LibraryImport';

function createMapStateToProps() {
  return createSelector(
    (state) => state.libraryImport,
    (state) => state.settings.rootFolders,
    (state, { match }) => parseInt(match.params.rootFolderId),
    (libraryImport, rootFolders, rootFolderId) => {
      const rootFolder = rootFolders.items.find((folder) => folder.id === rootFolderId);
      const rootFoldersFetching = rootFolders.isFetching;

      return {
        rootFolderId,
        rootFolder,
        rootFoldersFetching,
        rootFolderPath: rootFolder ? rootFolder.path : '',
        isPopulated: libraryImport.isPopulated,

        // The rows come out of the root folder fetch, so its failure is this
        // page's failure. Both are held back while a fetch is in flight: the
        // list being answered from may be a stale one that predates this root
        // folder, and saying "not found" about it would be premature.
        error: rootFoldersFetching ? null : rootFolders.error,
        rootFolderNotFound: !rootFoldersFetching && rootFolders.isPopulated && !rootFolder,
        items: libraryImport.items,
        isImporting: libraryImport.isImporting,
        importError: libraryImport.importError,
        skippedCount: libraryImport.skippedCount,
        defaults: libraryImport.defaults
      };
    }
  );
}

const mapDispatchToProps = {
  dispatchFetchRootFolders: fetchRootFolders,
  dispatchSetLibraryImportFolders: setLibraryImportFolders,
  dispatchQueueLookupAuthor: queueLookupAuthor,
  dispatchSetLibraryImportValue: setLibraryImportValue,
  dispatchSetLibraryImportDefault: setLibraryImportDefault,
  dispatchImportAuthors: importAuthors,
  dispatchClearLibraryImport: clearLibraryImport
};

class LibraryImportConnector extends Component {

  //
  // Lifecycle

  componentDidUpdate() {
    const {
      rootFolder,
      rootFoldersFetching,
      isPopulated
    } = this.props;

    // The unmapped folder list arrives as part of the root folder resource, so
    // the rows can only be seeded once the fetch started in onMount lands.
    // Seeding happens once per visit: isPopulated stays set until unmount, so
    // a later change to the root folder (the import itself trims its unmapped
    // list) doesn't rebuild the table and throw away every lookup.
    if (rootFolder && !rootFoldersFetching && !isPopulated) {
      this.seedFolders();
    }
  }

  //
  // Control

  seedFolders = () => {
    const { rootFolderId, rootFolder } = this.props;

    this.props.dispatchSetLibraryImportFolders({
      rootFolderId,
      unmappedFolders: rootFolder.unmappedFolders || []
    });
  };

  //
  // Listeners

  // Always refetch rather than seed from whatever is in the store. Which
  // folders are unmapped is decided by the server from what is on disk and
  // which authors exist, and a list cached from an earlier visit goes stale
  // the moment anything is imported — coming back to the page would offer the
  // folders that were just imported all over again.
  onMount = () => {
    this.props.dispatchFetchRootFolders();
  };

  onUnmount = () => {
    this.props.dispatchClearLibraryImport();
  };

  onQueueLookup = (payload) => {
    this.props.dispatchQueueLookupAuthor(payload);
  };

  onSetValue = (payload) => {
    this.props.dispatchSetLibraryImportValue(payload);
  };

  onInputChange = (payload) => {
    this.props.dispatchSetLibraryImportDefault(payload);
  };

  onImportPress = (ids) => {
    this.props.dispatchImportAuthors({ ids });
  };

  //
  // Render

  render() {
    const {
      dispatchFetchRootFolders,
      dispatchSetLibraryImportFolders,
      dispatchQueueLookupAuthor,
      dispatchSetLibraryImportValue,
      dispatchSetLibraryImportDefault,
      dispatchImportAuthors,
      dispatchClearLibraryImport,
      rootFolder,
      rootFolderId,
      rootFoldersFetching,
      ...otherProps
    } = this.props;

    return (
      <LibraryImport
        {...otherProps}
        onMount={this.onMount}
        onUnmount={this.onUnmount}
        onQueueLookup={this.onQueueLookup}
        onSetValue={this.onSetValue}
        onInputChange={this.onInputChange}
        onImportPress={this.onImportPress}
      />
    );
  }
}

LibraryImportConnector.propTypes = {
  rootFolderId: PropTypes.number.isRequired,
  rootFolder: PropTypes.object,
  rootFoldersFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  dispatchFetchRootFolders: PropTypes.func.isRequired,
  dispatchSetLibraryImportFolders: PropTypes.func.isRequired,
  dispatchQueueLookupAuthor: PropTypes.func.isRequired,
  dispatchSetLibraryImportValue: PropTypes.func.isRequired,
  dispatchSetLibraryImportDefault: PropTypes.func.isRequired,
  dispatchImportAuthors: PropTypes.func.isRequired,
  dispatchClearLibraryImport: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(LibraryImportConnector);
