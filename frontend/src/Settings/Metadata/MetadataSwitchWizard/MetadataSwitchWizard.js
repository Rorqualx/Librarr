import PropTypes from 'prop-types';
import React, { Component } from 'react';
import FieldSet from 'Components/FieldSet';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import styles from './MetadataSwitchWizard.css';

// Phase 5 minimum-viable wizard. The master plan calls for a 4-step UX
// (explain → run → review-low-confidence → commit). For the LLM-session
// MVP, this is a single-screen panel that explains the migration, runs
// the ReidentifyLibrary backend command, surfaces progress, and points
// the user at the low-confidence rows after completion. Polishing the
// multi-step UX (including a dedicated review screen that calls into
// BookIdMappingRepository.GetLowConfidence) is a Phase 9 polish task.
//
// A run has three ways to end and each gets its own screen: the command
// could not be queued at all (back to the intro, with the reason), it ran
// and failed (STEP_FAILED — the command now does this when lookups could
// not reach Open Library), or it completed (STEP_DONE).

const STEP_INTRO = 'intro';
const STEP_RUNNING = 'running';
const STEP_DONE = 'done';
const STEP_FAILED = 'failed';

function getStartErrorMessage(xhr) {
  const json = xhr && xhr.responseJSON;

  if (json && typeof json.message === 'string' && json.message) {
    return `Reidentify could not be started: ${json.message}`;
  }

  return 'Reidentify could not be started. Check that Librarr is reachable and try again.';
}

class MetadataSwitchWizard extends Component {

  constructor(props, context) {
    super(props, context);

    this._isMounted = false;

    this.state = {
      step: STEP_INTRO,
      startError: null
    };
  }

  componentDidMount() {
    this._isMounted = true;
  }

  componentDidUpdate(prevProps) {
    // If a reidentify command was running on the previous render but
    // is no longer, the run is over — one way or the other.
    if (prevProps.isReidentifying && !this.props.isReidentifying && this.state.step === STEP_RUNNING) {
      this.setState({ step: this.props.isReidentifyFailed ? STEP_FAILED : STEP_DONE });

      // A failed run still wrote every mapping it got to before it
      // stopped, so the review panel is stale in both cases.
      if (this.props.onReidentifyFinished) {
        this.props.onReidentifyFinished();
      }
    }
  }

  componentWillUnmount() {
    this._isMounted = false;
  }

  onStartPress = () => {
    this.setState({ step: STEP_RUNNING, startError: null });

    const request = this.props.onStartReidentify();

    // The running step is only left when a command is seen to stop
    // executing. If the POST itself fails there is no command, so nothing
    // would ever move it on.
    if (request && typeof request.then === 'function') {
      request.then(undefined, (xhr) => {
        if (!this._isMounted) {
          return;
        }

        this.setState({
          step: STEP_INTRO,
          startError: getStartErrorMessage(xhr)
        });
      });
    }
  };

  onResetPress = () => {
    this.setState({ step: STEP_INTRO, startError: null });
  };

  render() {
    const { isReidentifying } = this.props;
    const {
      step,
      startError
    } = this.state;

    return (
      <FieldSet legend={translate('SwitchMetadataSource')}>
        <div className={styles.body}>
          {step === STEP_INTRO && (
            <div>
              <p>
                Migrate your library from the legacy BookInfo (Goodreads-derived)
                metadata source to Open Library.
              </p>
              <ul>
                <li>Existing authors and books will be matched against Open Library by ISBN-13 → ASIN → title+author.</li>
                <li>Low-confidence matches (confidence &lt; 0.70) are logged for manual review.</li>
                <li>Files, monitoring state, profiles, and history are preserved — only the foreign IDs change.</li>
                <li>This is reversible: BookInfo can be re-selected via Settings → Metadata at any time.</li>
              </ul>
              {startError && (
                <p className={styles.error} role="alert">{startError}</p>
              )}
              <Button
                kind={kinds.PRIMARY}
                onPress={this.onStartPress}
                isDisabled={isReidentifying}
              >
                Start reidentify
              </Button>
            </div>
          )}

          {step === STEP_RUNNING && (
            <div className={styles.running}>
              <LoadingIndicator />
              <p>
                Reidentifying your library against Open Library. This may take a few
                minutes for large libraries. Progress is logged under System → Logs.
              </p>
            </div>
          )}

          {step === STEP_DONE && (
            <div>
              <p>Reidentification finished.</p>
              <p>
                Books and authors that matched with confidence below 0.70 are
                listed in the &ldquo;Low-confidence metadata mappings&rdquo;
                panel below — enter the correct Open Library ID and save to
                lock them as Manual overrides.
              </p>
              <Button onPress={this.onResetPress}>
                Run again
              </Button>
            </div>
          )}

          {step === STEP_FAILED && (
            <div>
              <p className={styles.error} role="alert">
                Reidentification did not finish.
              </p>
              <p>
                The usual cause is that Open Library could not be reached for
                some lookups. Everything matched before it stopped has been
                kept, and running it again only looks up what is still
                missing. The reason is recorded under System → Logs.
              </p>
              <p>
                Matches found so far with confidence below 0.70 are listed in
                the &ldquo;Low-confidence metadata mappings&rdquo; panel below.
              </p>
              <Button
                kind={kinds.PRIMARY}
                onPress={this.onStartPress}
                isDisabled={isReidentifying}
              >
                Run again
              </Button>
            </div>
          )}
        </div>
      </FieldSet>
    );
  }
}

MetadataSwitchWizard.propTypes = {
  isReidentifying: PropTypes.bool.isRequired,
  isReidentifyFailed: PropTypes.bool,
  onStartReidentify: PropTypes.func.isRequired,
  onReidentifyFinished: PropTypes.func
};

export default MetadataSwitchWizard;
