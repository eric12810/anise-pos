import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';

const source = readFileSync(new URL('../src/AnisePOS.Api/wwwroot/caller-notifications.js', import.meta.url), 'utf8');
const harness = readFileSync(new URL('./caller-navigation-harness.js', import.meta.url), 'utf8');

test('caller navigation lifecycle and order handoff', () => {
  const checks = runInNewContext(harness, { callerSource: source });
  assert.equal(checks, 25);
});
