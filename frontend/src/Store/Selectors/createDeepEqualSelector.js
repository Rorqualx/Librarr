import _ from 'lodash';
import { createSelectorCreator, lruMemoize } from 'reselect';

const createDeepEqualSelector = createSelectorCreator(
  lruMemoize,
  _.isEqual
);

export default createDeepEqualSelector;
