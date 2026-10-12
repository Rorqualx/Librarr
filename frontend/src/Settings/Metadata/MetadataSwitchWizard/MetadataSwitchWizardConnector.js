import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommandHelper } from 'Store/Actions/commandActions';
import createCommandSelector from 'Store/Selectors/createCommandSelector';
import { isCommandExecuting, isCommandFailed } from 'Utilities/Command';
import MetadataSwitchWizard from './MetadataSwitchWizard';

function createMapStateToProps() {
  return createSelector(
    createCommandSelector(commandNames.REIDENTIFY_LIBRARY),
    (command) => {
      return {
        isReidentifying: isCommandExecuting(command),
        isReidentifyFailed: isCommandFailed(command)
      };
    }
  );
}

// executeCommandHelper rather than the executeCommand thunk: the thunk
// discards the request, and the wizard has to know when the POST itself
// fails or it waits forever on a command that was never queued.
function createMapDispatchToProps(dispatch) {
  return {
    dispatchExecuteReidentify() {
      return executeCommandHelper({ name: commandNames.REIDENTIFY_LIBRARY }, dispatch);
    }
  };
}

class MetadataSwitchWizardConnector extends Component {

  onStartReidentify = () => {
    return this.props.dispatchExecuteReidentify();
  };

  render() {
    const {
      dispatchExecuteReidentify,
      ...otherProps
    } = this.props;

    return (
      <MetadataSwitchWizard
        {...otherProps}
        onStartReidentify={this.onStartReidentify}
      />
    );
  }
}

MetadataSwitchWizardConnector.propTypes = {
  isReidentifying: PropTypes.bool.isRequired,
  isReidentifyFailed: PropTypes.bool.isRequired,
  dispatchExecuteReidentify: PropTypes.func.isRequired,
  onReidentifyFinished: PropTypes.func
};

export default connect(createMapStateToProps, createMapDispatchToProps)(MetadataSwitchWizardConnector);
