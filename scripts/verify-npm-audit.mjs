#!/usr/bin/env node

import {spawnSync} from 'node:child_process';
import {createHash} from 'node:crypto';
import {existsSync, readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

const ALLOWLIST_EXPIRES_AT = Date.parse('2026-09-30T00:00:00Z');
const EXPECTED_ADVISORIES = new Set([
  'https://github.com/advisories/GHSA-5p2g-fcmc-qvqq',
  'https://github.com/advisories/GHSA-w3rx-r6r6-pgpr',
]);
// npm audit has emitted several projections of the same installed dependency graph.
// These fingerprints cover the report version, complete metadata, and every vulnerability field,
// after recursively sorting object keys and set-like arrays. They were reviewed
// against clean npm-ci installs of the committed lockfile on 2026-08-15.
const EXPECTED_AUDIT_PROFILES = new Map([
  [
    '4afc2cd320fc56796f3e67cf03fb44c937ca9220f5775387f5d64b34432fab41',
    'exact 17-package installed-tree profile',
  ],
  [
    'd1c620a28d532eb62a38da2e2f410262cd3cf8c98dc3b1f340dc573d18e0c150',
    'exact 18-package profile with search-local rooted through theme-common',
  ],
  [
    '6bc04ae284f202b83caa55299936c7006f90c64bb32fc6270c3b8eac6710c3e6',
    'exact 18-package profile with search-local rooted through content-docs',
  ],
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

function canonicalize(value) {
  if (Array.isArray(value)) {
    return value
      .map(canonicalize)
      .sort((left, right) => {
        const leftJson = JSON.stringify(left);
        const rightJson = JSON.stringify(right);
        return leftJson < rightJson ? -1 : leftJson > rightJson ? 1 : 0;
      });
  }
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, canonicalize(value[key])]),
    );
  }
  return value;
}

function auditProfileFingerprint(audit) {
  const profile = canonicalize(audit);
  return createHash('sha256').update(JSON.stringify(profile)).digest('hex');
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
    && dependencies.some((dependency) => reachesImageSize(dependency, entries, new Set(visiting)));
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
  for (const [name, vulnerability] of Object.entries(entries)) {
    if (vulnerability.severity !== 'high') fail(`${name} changed severity.`);
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
      if (vulnerability.via.some((item) => !actualNames.has(item))) {
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
    || counts?.high !== actualNames.size
    || counts?.critical !== 0
    || counts?.total !== actualNames.size
  ) {
    fail(`Unexpected npm audit counts: ${JSON.stringify(counts)}.`);
  }

  const fingerprint = auditProfileFingerprint(audit);
  const profileName = EXPECTED_AUDIT_PROFILES.get(fingerprint);
  if (profileName === undefined) {
    fail(
      `Unexpected vulnerable package closure/profile ${fingerprint}: `
      + `${JSON.stringify([...actualNames].sort())}.`,
    );
  }
  return profileName;
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
      const current = changed.vulnerabilities['@docusaurus/core'].range;
      changed.vulnerabilities['@docusaurus/core'].range = current === '' ? '*' : '';
    },
    'a hybrid npm audit profile was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['image-size'].nodes.push('node_modules/unreviewed/image-size');
    },
    'changed full audit metadata was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.auditReportVersion += 1;
    },
    'a changed audit report schema was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.metadata.dependencies.prod += 1;
      changed.metadata.dependencies.total += 1;
    },
    'changed dependency counts were accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/core'].via.push('@docusaurus/theme-common');
    },
    'a changed allowlisted dependency edge was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['image-size'].via[0].range = '<2.0.2';
    },
    'changed advisory metadata with the same URL was accepted',
  );
}

export {runNegativeSelfTests, validateAudit, validateDependencyPolicy};

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
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
    const profileName = validateAudit(audit);
    runNegativeSelfTests(audit);
    console.log(
      'npm audit contains only the two unpatched image-size denial-of-service advisories '
      + `and their reviewed ${profileName}; `
      + 'exception expires 2026-09-30.',
    );
  } catch (error) {
    console.error(`npm audit verification failed: ${error.message}`);
    process.exitCode = 1;
  }
}
