#!/usr/bin/env node

import {spawnSync} from 'node:child_process';
import {existsSync, readFileSync} from 'node:fs';
import {resolve} from 'node:path';

const ALLOWLIST_EXPIRES_AT = Date.parse('2026-09-30T00:00:00Z');
const EXPECTED_ADVISORIES = new Set([
  'https://github.com/advisories/GHSA-5p2g-fcmc-qvqq',
  'https://github.com/advisories/GHSA-w3rx-r6r6-pgpr',
]);
const EXPECTED_FIXABILITY = new Map([
  ['@docusaurus/core', false],
  ['@docusaurus/mdx-loader', false],
  ['@docusaurus/plugin-content-blog', false],
  ['@docusaurus/plugin-content-docs', true],
  ['@docusaurus/plugin-content-pages', true],
  ['@docusaurus/plugin-css-cascade-layers', false],
  ['@docusaurus/plugin-debug', true],
  ['@docusaurus/plugin-google-analytics', true],
  ['@docusaurus/plugin-google-gtag', true],
  ['@docusaurus/plugin-google-tag-manager', false],
  ['@docusaurus/plugin-sitemap', true],
  ['@docusaurus/plugin-svgr', false],
  ['@docusaurus/preset-classic', false],
  ['@docusaurus/theme-classic', true],
  ['@docusaurus/theme-common', false],
  ['@docusaurus/theme-search-algolia', true],
  ['image-size', false],
]);

function fail(message) {
  throw new Error(message);
}

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

function sameSet(actual, expected) {
  return actual.size === expected.size && [...actual].every((item) => expected.has(item));
}

function validateDependencyPolicy(packageJson, packageLock) {
  const exactDocusaurus = [
    ['dependencies', '@docusaurus/core'],
    ['dependencies', '@docusaurus/preset-classic'],
    ['devDependencies', '@docusaurus/module-type-aliases'],
    ['devDependencies', '@docusaurus/tsconfig'],
    ['devDependencies', '@docusaurus/types'],
  ];
  for (const [group, name] of exactDocusaurus) {
    if (packageJson[group]?.[name] !== '3.10.2') {
      fail(`${name} must be pinned exactly to 3.10.2.`);
    }
    if (packageLock.packages?.[`node_modules/${name}`]?.version !== '3.10.2') {
      fail(`${name} is not locked to 3.10.2.`);
    }
  }
  const expectedOverrides = { 'serialize-javascript': '7.0.5', uuid: '11.1.1' };
  if (JSON.stringify(packageJson.overrides) !== JSON.stringify(expectedOverrides)) {
    fail(`Unexpected npm overrides: ${JSON.stringify(packageJson.overrides)}.`);
  }
  for (const [name, version] of Object.entries(expectedOverrides)) {
    if (packageLock.packages?.[`node_modules/${name}`]?.version !== version) {
      fail(`${name} is not locked to the audited override ${version}.`);
    }
  }
  if (packageLock.lockfileVersion !== 3) {
    fail(`Expected npm lockfileVersion 3, got ${packageLock.lockfileVersion}.`);
  }
}

function reachesImageSize(name, entries, visiting = new Set()) {
  if (name === 'image-size') return true;
  if (visiting.has(name)) return false;
  visiting.add(name);
  const via = entries[name]?.via ?? [];
  const dependencies = via.filter((item) => typeof item === 'string');
  return dependencies.length > 0
    && dependencies.every((dependency) => reachesImageSize(dependency, entries, new Set(visiting)));
}

function validateAudit(audit, now = Date.now()) {
  if (now >= ALLOWLIST_EXPIRES_AT) {
    fail('The temporary image-size advisory exception expired on 2026-09-30.');
  }
  const entries = audit.vulnerabilities;
  if (entries === null || typeof entries !== 'object' || Array.isArray(entries)) {
    fail('npm audit returned no vulnerabilities object.');
  }
  const actualNames = new Set(Object.keys(entries));
  const expectedNames = new Set(EXPECTED_FIXABILITY.keys());
  if (!sameSet(actualNames, expectedNames)) {
    fail(`Unexpected vulnerable package closure: ${JSON.stringify([...actualNames].sort())}.`);
  }

  for (const [name, expectedFixAvailable] of EXPECTED_FIXABILITY) {
    const vulnerability = entries[name];
    if (vulnerability.severity !== 'high' || vulnerability.range !== '*') {
      fail(`${name} changed severity or vulnerable range.`);
    }
    if (vulnerability.fixAvailable !== expectedFixAvailable) {
      fail(`${name} fixAvailable changed from the reviewed value ${expectedFixAvailable}.`);
    }
    if (!Array.isArray(vulnerability.via)) {
      fail(`${name} has an invalid dependency/advisory chain.`);
    }
    if (name === 'image-size') {
      if (vulnerability.via.length !== 2) {
        fail(`image-size must contain exactly the two reviewed advisories.`);
      }
      const advisoryUrls = new Set(
        vulnerability.via
          .filter((item) => typeof item === 'object' && item !== null)
          .map((item) => item.url),
      );
      if (!sameSet(advisoryUrls, EXPECTED_ADVISORIES)) {
        fail(`image-size advisory set changed: ${JSON.stringify([...advisoryUrls])}.`);
      }
      if (vulnerability.via.some((item) => typeof item === 'string')) {
        fail('image-size unexpectedly depends on another vulnerable package.');
      }
    } else {
      if (vulnerability.via.some((item) => typeof item !== 'string')) {
        fail(`${name} contains a direct advisory instead of only the image-size dependency chain.`);
      }
      if (vulnerability.via.some((item) => !EXPECTED_FIXABILITY.has(item))) {
        fail(`${name} depends on an unexpected vulnerable package: ${JSON.stringify(vulnerability.via)}.`);
      }
      if (!reachesImageSize(name, entries)) {
        fail(`${name} does not resolve exclusively to the allowlisted image-size advisories.`);
      }
    }
  }

  const counts = audit.metadata?.vulnerabilities;
  if (
    counts?.info !== 0
    || counts?.low !== 0
    || counts?.moderate !== 0
    || counts?.high !== 17
    || counts?.critical !== 0
    || counts?.total !== 17
  ) {
    fail(`Unexpected npm audit counts: ${JSON.stringify(counts)}.`);
  }
}

function expectRejected(audit, mutate, message) {
  const changed = structuredClone(audit);
  mutate(changed);

  try {
    validateAudit(changed);
  } catch {
    return;
  }

  fail(`Audit verifier self-test failed: ${message}.`);
}

function runNegativeSelfTests(audit) {
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['unexpected-wrapper'] = {
        name: 'unexpected-wrapper',
        severity: 'high',
        isDirect: true,
        via: ['image-size'],
        effects: [],
        range: '',
        nodes: ['node_modules/unexpected-wrapper'],
        fixAvailable: false,
      };
      changed.metadata.vulnerabilities.high += 1;
      changed.metadata.vulnerabilities.total += 1;
    },
    'an unreviewed wrapper was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/core'].via.push({
        source: 999999,
        name: '@docusaurus/core',
        dependency: '@docusaurus/core',
        title: 'unreviewed advisory',
        url: 'https://github.com/advisories/GHSA-xxxx-xxxx-xxxx',
        severity: 'high',
        range: '*',
      });
    },
    'an unreviewed direct advisory on a dependency wrapper was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/core'].range = '';
    },
    'a lock-only empty-range profile was accepted as installed-tree evidence',
  );
}

export {runNegativeSelfTests, validateAudit, validateDependencyPolicy};

if (process.argv[1] && resolve(process.argv[1]) === resolve(new URL(import.meta.url).pathname)) {
  try {
    const websiteRoot = process.cwd();
    const installedLockPath = resolve(websiteRoot, 'node_modules', '.package-lock.json');
    if (!existsSync(installedLockPath)) {
      fail('Installed-tree audit requires `npm ci` and node_modules/.package-lock.json.');
    }
    validateDependencyPolicy(
      readJson(resolve(websiteRoot, 'package.json')),
      readJson(resolve(websiteRoot, 'package-lock.json')),
    );
    validateDependencyPolicy(
      readJson(resolve(websiteRoot, 'package.json')),
      readJson(installedLockPath),
    );
    const npmCommand = process.platform === 'win32' ? 'npm.cmd' : 'npm';
    const auditProcess = spawnSync(npmCommand, ['audit', '--json'], {
      cwd: websiteRoot,
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
    });
    if (auditProcess.error) fail(`Could not execute npm audit: ${auditProcess.error.message}`);
    if (![0, 1].includes(auditProcess.status)) {
      fail(`npm audit exited ${auditProcess.status}: ${auditProcess.stderr}`);
    }
    const audit = JSON.parse(auditProcess.stdout);
    validateAudit(audit);
    runNegativeSelfTests(audit);
    console.log(
      'npm audit contains only the two unpatched image-size denial-of-service advisories '
      + 'and their exact 17-package installed-tree Docusaurus dependency closure; '
      + 'exception expires 2026-09-30.',
    );
  } catch (error) {
    console.error(`npm audit verification failed: ${error.message}`);
    process.exitCode = 1;
  }
}
