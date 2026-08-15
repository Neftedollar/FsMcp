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
const IMAGE_SIZE_ADVISORY_FINGERPRINT =
  '20b851923e893e086bfbd226bbe4e012837a39024324b510aae29259315b0b07';
const SEARCH_LOCAL = '@easyops-cn/docusaurus-search-local';
// npm audit varies derived `effects` and `fixAvailable` projections even for an
// unchanged lockfile. The security boundary is the exact package closure, direct
// package identity, reviewed dependency edges, and exact leaf advisory objects.
const EXPECTED_WRAPPERS = new Map([
  ['@docusaurus/core', { isDirect: true, via: [['@docusaurus/mdx-loader']] }],
  ['@docusaurus/mdx-loader', { isDirect: false, via: [['image-size']] }],
  ['@docusaurus/plugin-content-blog', {
    isDirect: false,
    via: [
      ['@docusaurus/core', '@docusaurus/mdx-loader', '@docusaurus/theme-common'],
      ['@docusaurus/core', '@docusaurus/mdx-loader', '@docusaurus/plugin-content-docs', '@docusaurus/theme-common'],
    ],
  }],
  ['@docusaurus/plugin-content-docs', {
    isDirect: false,
    via: [['@docusaurus/core', '@docusaurus/mdx-loader', '@docusaurus/theme-common']],
  }],
  ['@docusaurus/plugin-content-pages', {
    isDirect: false,
    via: [['@docusaurus/core', '@docusaurus/mdx-loader']],
  }],
  ['@docusaurus/plugin-css-cascade-layers', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/plugin-debug', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/plugin-google-analytics', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/plugin-google-gtag', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/plugin-google-tag-manager', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/plugin-sitemap', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/plugin-svgr', { isDirect: false, via: [['@docusaurus/core']] }],
  ['@docusaurus/preset-classic', {
    isDirect: true,
    via: [[
      '@docusaurus/core',
      '@docusaurus/plugin-content-blog',
      '@docusaurus/plugin-content-docs',
      '@docusaurus/plugin-content-pages',
      '@docusaurus/plugin-css-cascade-layers',
      '@docusaurus/plugin-debug',
      '@docusaurus/plugin-google-analytics',
      '@docusaurus/plugin-google-gtag',
      '@docusaurus/plugin-google-tag-manager',
      '@docusaurus/plugin-sitemap',
      '@docusaurus/plugin-svgr',
      '@docusaurus/theme-classic',
      '@docusaurus/theme-common',
      '@docusaurus/theme-search-algolia',
    ]],
  }],
  ['@docusaurus/theme-classic', {
    isDirect: false,
    via: [[
      '@docusaurus/core',
      '@docusaurus/mdx-loader',
      '@docusaurus/plugin-content-blog',
      '@docusaurus/plugin-content-docs',
      '@docusaurus/plugin-content-pages',
      '@docusaurus/theme-common',
    ]],
  }],
  ['@docusaurus/theme-common', {
    isDirect: false,
    via: [
      ['@docusaurus/mdx-loader'],
      ['@docusaurus/mdx-loader', '@docusaurus/plugin-content-docs'],
    ],
  }],
  ['@docusaurus/theme-search-algolia', {
    isDirect: false,
    via: [['@docusaurus/core', '@docusaurus/plugin-content-docs', '@docusaurus/theme-common']],
  }],
  [SEARCH_LOCAL, {
    isDirect: true,
    via: [['@docusaurus/plugin-content-docs', '@docusaurus/theme-common']],
  }],
]);
const EXPECTED_BASE_NAMES = new Set(
  [...EXPECTED_WRAPPERS.keys()].filter((name) => name !== SEARCH_LOCAL).concat('image-size'),
);
const EXPECTED_WITH_SEARCH_NAMES = new Set([...EXPECTED_BASE_NAMES, SEARCH_LOCAL]);
const EXPECTED_DEPENDENCY_COUNTS = {
  prod: 1293,
  dev: 1,
  optional: 21,
  peer: 1,
  peerOptional: 0,
  total: 1315,
};

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

function fingerprint(value) {
  return createHash('sha256').update(JSON.stringify(canonicalize(value))).digest('hex');
}

function hasExactKeys(value, expectedKeys) {
  return sameSet(new Set(Object.keys(value ?? {})), new Set(expectedKeys));
}

function sameCanonical(left, right) {
  return JSON.stringify(canonicalize(left)) === JSON.stringify(canonicalize(right));
}

function matchesOneStringSet(actual, alternatives) {
  const actualSet = new Set(actual);
  return actualSet.size === actual.length
    && alternatives.some((alternative) => sameSet(actualSet, new Set(alternative)));
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
  if (!hasExactKeys(audit, ['auditReportVersion', 'vulnerabilities', 'metadata'])) {
    fail('npm audit report shape changed.');
  }
  if (audit.auditReportVersion !== 2) {
    fail(`Unexpected npm audit report version ${audit.auditReportVersion}.`);
  }
  if (!hasExactKeys(audit.metadata, ['vulnerabilities', 'dependencies'])) {
    fail('npm audit metadata shape changed.');
  }
  if (!sameCanonical(audit.metadata.dependencies, EXPECTED_DEPENDENCY_COUNTS)) {
    fail(`Unexpected npm dependency counts: ${JSON.stringify(audit.metadata.dependencies)}.`);
  }

  const entries = audit.vulnerabilities;
  if (entries === null || typeof entries !== 'object' || Array.isArray(entries)) {
    fail('npm audit returned no vulnerabilities object.');
  }
  const actualNames = new Set(Object.keys(entries));
  const hasSearchLocal = actualNames.has(SEARCH_LOCAL);
  const expectedNames = hasSearchLocal ? EXPECTED_WITH_SEARCH_NAMES : EXPECTED_BASE_NAMES;
  if (!sameSet(actualNames, expectedNames)) {
    fail(`Unexpected vulnerable package closure: ${JSON.stringify([...actualNames].sort())}.`);
  }

  const outerRanges = new Set();
  for (const [name, vulnerability] of Object.entries(entries)) {
    if (!hasExactKeys(
      vulnerability,
      ['name', 'severity', 'isDirect', 'via', 'effects', 'range', 'nodes', 'fixAvailable'],
    )) {
      fail(`${name} vulnerability shape changed.`);
    }
    if (vulnerability.name !== name) fail(`${name} has a mismatched package name.`);
    if (vulnerability.severity !== 'high') fail(`${name} changed severity.`);
    if (!['', '*'].includes(vulnerability.range)) fail(`${name} changed vulnerable range projection.`);
    outerRanges.add(vulnerability.range);
    if (!sameCanonical(vulnerability.nodes, [`node_modules/${name}`])) {
      fail(`${name} changed installed node paths.`);
    }
    if (typeof vulnerability.fixAvailable !== 'boolean') {
      fail(`${name} has an invalid fixAvailable projection.`);
    }
    if (!Array.isArray(vulnerability.effects)
      || new Set(vulnerability.effects).size !== vulnerability.effects.length
      || vulnerability.effects.some((effect) => typeof effect !== 'string' || !actualNames.has(effect))) {
      fail(`${name} has an invalid effects projection.`);
    }
    if (!Array.isArray(vulnerability.via)) {
      fail(`${name} has an invalid dependency/advisory chain.`);
    }
    if (name === 'image-size') {
      if (vulnerability.isDirect !== false || vulnerability.fixAvailable !== false) {
        fail('image-size directness or fixability changed.');
      }
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
      if (fingerprint(vulnerability.via) !== IMAGE_SIZE_ADVISORY_FINGERPRINT) {
        fail('image-size advisory metadata changed.');
      }
    } else {
      const expected = EXPECTED_WRAPPERS.get(name);
      if (expected === undefined || vulnerability.isDirect !== expected.isDirect) {
        fail(`${name} direct dependency classification changed.`);
      }
      if (expected.isDirect && vulnerability.fixAvailable !== false) {
        fail(`${name} unexpectedly gained an available direct remediation.`);
      }
      if (vulnerability.via.some((item) => typeof item !== 'string')) {
        fail(`${name} contains a direct advisory instead of only the image-size dependency chain.`);
      }
      if (!matchesOneStringSet(vulnerability.via, expected.via)) {
        fail(`${name} changed reviewed dependency edges: ${JSON.stringify(vulnerability.via)}.`);
      }
      if (!reachesImageSize(name, entries)) {
        fail(`${name} does not resolve exclusively to the allowlisted image-size advisories.`);
      }
    }
  }
  if (outerRanges.size !== 1) {
    fail(`npm audit mixed vulnerable range projections: ${JSON.stringify([...outerRanges])}.`);
  }

  const counts = audit.metadata?.vulnerabilities;
  if (!hasExactKeys(counts, ['info', 'low', 'moderate', 'high', 'critical', 'total'])) {
    fail('npm audit vulnerability count shape changed.');
  }
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
  return hasSearchLocal
    ? 'exact 18-package closure including the search-local wrapper'
    : 'exact 17-package closure';
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
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/core'].effects.push('unreviewed-package');
    },
    'an effect outside the exact vulnerable package closure was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/core'].fixAvailable = 'unknown';
    },
    'an invalid derived fixAvailable value was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/core'].fixAvailable = true;
    },
    'an available remediation for a direct dependency was accepted',
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
