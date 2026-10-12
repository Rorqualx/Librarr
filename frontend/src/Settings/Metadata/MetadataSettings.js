import React, { Component } from 'react';
import PageContent from 'Components/Page/PageContent';
import PageContentBody from 'Components/Page/PageContentBody';
import SettingsToolbarConnector from 'Settings/SettingsToolbarConnector';
import translate from 'Utilities/String/translate';
import LowConfidenceMappings from './LowConfidenceMappings/LowConfidenceMappings';
// import MetadatasConnector from './Metadata/MetadatasConnector';
import MetadataProviderConnector from './MetadataProvider/MetadataProviderConnector';
import MetadataSwitchWizardConnector from './MetadataSwitchWizard/MetadataSwitchWizardConnector';

class MetadataSettings extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._saveCallback = null;

    this.state = {
      isSaving: false,
      hasPendingChanges: false,
      // Bumped each time a reidentify run ends, so the review panel
      // below reloads instead of showing the list from before the run.
      mappingsReloadToken: 0
    };
  }

  //
  // Listeners

  onChildMounted = (saveCallback) => {
    this._saveCallback = saveCallback;
  };

  onChildStateChange = (payload) => {
    this.setState(payload);
  };

  onReidentifyFinished = () => {
    this.setState((state) => ({ mappingsReloadToken: state.mappingsReloadToken + 1 }));
  };

  onSavePress = () => {
    if (this._saveCallback) {
      this._saveCallback();
    }
  };

  //
  // Render
  render() {
    const {
      isSaving,
      hasPendingChanges,
      mappingsReloadToken
    } = this.state;

    return (
      <PageContent title={translate('MetadataSettings')}>
        <SettingsToolbarConnector
          isSaving={isSaving}
          hasPendingChanges={hasPendingChanges}
          onSavePress={this.onSavePress}
        />

        <PageContentBody>
          <MetadataProviderConnector
            onChildMounted={this.onChildMounted}
            onChildStateChange={this.onChildStateChange}
          />
          <MetadataSwitchWizardConnector onReidentifyFinished={this.onReidentifyFinished} />
          <LowConfidenceMappings reloadToken={mappingsReloadToken} />
          {/* <MetadatasConnector /> */}
        </PageContentBody>
      </PageContent>
    );
  }
}

export default MetadataSettings;
