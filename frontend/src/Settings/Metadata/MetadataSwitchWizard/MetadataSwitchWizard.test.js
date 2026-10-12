import { fireEvent, render, screen } from '@testing-library/react';
import $ from 'jquery';
import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import MetadataSwitchWizard from './MetadataSwitchWizard';

// A rejected jQuery promise runs its `then` handlers on a later tick, so the
// start-failure tests wait for the alert (findByRole) rather than expecting
// it synchronously.
//
// The wizard only knows a run is over by watching isReidentifying fall
// from true to false, so each test drives that prop the way the command
// store would: false (not yet queued) → true (queued/started) → false.

function setup(startResult) {
  const onStartReidentify = vi.fn(() => startResult);
  const onReidentifyFinished = vi.fn();

  const props = {
    isReidentifying: false,
    isReidentifyFailed: false,
    onStartReidentify,
    onReidentifyFinished
  };

  const view = render(<MetadataSwitchWizard {...props} />);

  return {
    onStartReidentify,
    onReidentifyFinished,
    update: (changes) => view.rerender(<MetadataSwitchWizard {...props} {...changes} />)
  };
}

describe('MetadataSwitchWizard', () => {
  it('reports completion and asks for the review panel to be reloaded', () => {
    const { update, onReidentifyFinished } = setup($.Deferred().resolve().promise());

    fireEvent.click(screen.getByText('Start reidentify'));
    update({ isReidentifying: true });

    expect(onReidentifyFinished).not.toHaveBeenCalled();

    update({ isReidentifying: false });

    expect(screen.getByText('Reidentification finished.')).toBeInTheDocument();
    expect(onReidentifyFinished).toHaveBeenCalledTimes(1);
  });

  // The command now ends as failed when lookups could not reach Open
  // Library. That is not "finished", and it is not "still running" either.
  it('reports a run that ended as failed, and still reloads the review panel', () => {
    const { update, onReidentifyFinished } = setup($.Deferred().resolve().promise());

    fireEvent.click(screen.getByText('Start reidentify'));
    update({ isReidentifying: true });
    update({ isReidentifying: false, isReidentifyFailed: true });

    expect(screen.getByRole('alert')).toHaveTextContent('Reidentification did not finish.');
    expect(screen.queryByText('Reidentification finished.')).not.toBeInTheDocument();
    expect(onReidentifyFinished).toHaveBeenCalledTimes(1);
  });

  it('can be run again straight from a failed run', () => {
    const { update, onStartReidentify } = setup($.Deferred().resolve().promise());

    fireEvent.click(screen.getByText('Start reidentify'));
    update({ isReidentifying: true });
    update({ isReidentifying: false, isReidentifyFailed: true });

    fireEvent.click(screen.getByText('Run again'));

    expect(onStartReidentify).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('returns to the start with the reason when the command cannot be queued', async() => {
    const request = $.Deferred();
    const { onReidentifyFinished } = setup(request.promise());

    fireEvent.click(screen.getByText('Start reidentify'));

    expect(screen.queryByText('Start reidentify')).not.toBeInTheDocument();

    request.reject({ status: 500, responseJSON: { message: 'Database is locked' } });

    expect(await screen.findByRole('alert')).toHaveTextContent('Reidentify could not be started: Database is locked');
    expect(screen.getByText('Start reidentify')).toBeInTheDocument();
    expect(onReidentifyFinished).not.toHaveBeenCalled();
  });

  it('gives a usable message when the failed request has no body', async() => {
    const request = $.Deferred();

    setup(request.promise());
    fireEvent.click(screen.getByText('Start reidentify'));

    request.reject({ status: 0 });

    expect(await screen.findByRole('alert')).toHaveTextContent('Reidentify could not be started.');
  });

  it('clears a start failure once a later attempt gets going', async() => {
    const request = $.Deferred();
    const { onStartReidentify } = setup(request.promise());

    fireEvent.click(screen.getByText('Start reidentify'));

    request.reject({ status: 500 });
    await screen.findByRole('alert');

    onStartReidentify.mockReturnValue($.Deferred().resolve().promise());
    fireEvent.click(screen.getByText('Start reidentify'));

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
