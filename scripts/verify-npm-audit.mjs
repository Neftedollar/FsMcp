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
const IMAGE_SIZE = 'image-size';
const IMAGE_SIZE_VERSION = '2.0.2';
const SEARCH_LOCAL = '@easyops-cn/docusaurus-search-local';
const SEARCH_LOCAL_VERSION = '0.55.3';
// npm 10.9.8 derives this breaking downgrade for the direct wrapper and, in
// another projection, its image-size leaf even though the exact leaf advisories
// remain unfixable. Accept only this value after proving the pin is still latest.
const SEARCH_LOCAL_DERIVED_FIX = {
  name: SEARCH_LOCAL,
  version: '0.29.0',
  isSemVerMajor: true,
};
// npm audit varies derived `effects` and `fixAvailable` projections even for an
// unchanged lockfile. The security boundary is the exact package closure, direct
// package identity, reviewed dependency edges, and exact leaf advisory objects.
const EXPECTED_WRAPPERS = new Map([
  ['@docusaurus/core', { isDirect: true, requireNoFix: true, via: [['@docusaurus/mdx-loader']] }],
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
    requireNoFix: true,
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
    requireNoFix: true,
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

function sameOrderedArray(left, right) {
  return Array.isArray(left)
    && Array.isArray(right)
    && left.length === right.length
    && left.every((value, index) => value === right[index]);
}

function matchesOneStringSet(actual, alternatives) {
  const actualSet = new Set(actual);
  return actualSet.size === actual.length
    && alternatives.some((alternative) => sameSet(actualSet, new Set(alternative)));
}

function isValidFixProjection(value, actualNames) {
  if (typeof value === 'boolean') return true;
  return value !== null
    && typeof value === 'object'
    && !Array.isArray(value)
    && hasExactKeys(value, ['name', 'version', 'isSemVerMajor'])
    && typeof value.name === 'string'
    && actualNames.has(value.name)
    && typeof value.version === 'string'
    && value.version.length > 0
    && typeof value.isSemVerMajor === 'boolean';
}

function parsePublishedVersion(value) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/.exec(value);
  return match?.slice(1, 4).map(Number);
}

function isGreaterVersion(left, right) {
  return left.some((part, index) => part > right[index]
    && left.slice(0, index).every((earlier, earlierIndex) => earlier === right[earlierIndex]));
}

function hasReviewedPackageRegistryEvidence(
  {latestVersion, publishedVersions} = {},
  reviewedVersion,
  requiredVersions = [],
) {
  if (latestVersion !== reviewedVersion
    || !Array.isArray(publishedVersions)
    || publishedVersions.length === 0
    || publishedVersions.some((version) => typeof version !== 'string')
    || !publishedVersions.includes(reviewedVersion)
    || requiredVersions.some((version) => !publishedVersions.includes(version))) {
    return false;
  }
  const published = publishedVersions.map(parsePublishedVersion);
  if (published.some((version) => version === undefined)) return false;
  const pinned = parsePublishedVersion(reviewedVersion);
  return published
    .every((version) => !isGreaterVersion(version, pinned));
}

function hasReviewedSearchLocalRegistryEvidence(registryEvidence) {
  return hasReviewedPackageRegistryEvidence(
    registryEvidence?.searchLocal,
    SEARCH_LOCAL_VERSION,
    [SEARCH_LOCAL_DERIVED_FIX.version],
  );
}

function hasReviewedImageSizeRegistryEvidence(registryEvidence) {
  return hasReviewedPackageRegistryEvidence(
    registryEvidence?.imageSize,
    IMAGE_SIZE_VERSION,
  );
}

function isReviewedDerivedFixProjection(value, registryEvidence) {
  if (!hasReviewedSearchLocalRegistryEvidence(registryEvidence)) return false;
  return value !== null
    && typeof value === 'object'
    && !Array.isArray(value)
    && hasExactKeys(value, ['name', 'version', 'isSemVerMajor'])
    && sameCanonical(value, SEARCH_LOCAL_DERIVED_FIX);
}

function validateDependencyPolicy(packageJson, packageLock, {requireRoot = true} = {}) {
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
  if (packageJson.dependencies?.[SEARCH_LOCAL] !== SEARCH_LOCAL_VERSION) {
    fail(`${SEARCH_LOCAL} must be pinned exactly to ${SEARCH_LOCAL_VERSION}.`);
  }
  if (packageLock.packages?.[`node_modules/${SEARCH_LOCAL}`]?.version !== SEARCH_LOCAL_VERSION) {
    fail(`${SEARCH_LOCAL} is not installed at the reviewed ${SEARCH_LOCAL_VERSION} version.`);
  }
  const lockedRoot = packageLock.packages?.[''];
  if (requireRoot && !lockedRoot) {
    fail('The source package lock is missing its root package entry.');
  }
  if (lockedRoot && lockedRoot.dependencies?.[SEARCH_LOCAL] !== SEARCH_LOCAL_VERSION) {
    fail(`${SEARCH_LOCAL} is not locked exactly in the root dependency graph.`);
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

function validateAudit(audit, now = Date.now(), registryEvidence = {}) {
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
  if (!hasReviewedImageSizeRegistryEvidence(registryEvidence)) {
    fail(
      `${IMAGE_SIZE} registry state changed: expected latest version ${IMAGE_SIZE_VERSION} `
      + `with no newer published version; received ${JSON.stringify(registryEvidence?.imageSize)}.`,
    );
  }

  for (const [name, vulnerability] of Object.entries(entries)) {
    if (!hasExactKeys(
      vulnerability,
      ['name', 'severity', 'isDirect', 'via', 'effects', 'range', 'nodes', 'fixAvailable'],
    )) {
      fail(`${name} vulnerability shape changed.`);
    }
    if (vulnerability.name !== name) fail(`${name} has a mismatched package name.`);
    if (vulnerability.severity !== 'high') fail(`${name} changed severity.`);
    if (typeof vulnerability.range !== 'string') fail(`${name} has an invalid vulnerable range projection.`);
    if (!sameCanonical(vulnerability.nodes, [`node_modules/${name}`])) {
      fail(`${name} changed installed node paths.`);
    }
    if (!isValidFixProjection(vulnerability.fixAvailable, actualNames)) {
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
      if (vulnerability.isDirect !== false) {
        fail(
          `image-size directness changed: ${JSON.stringify(vulnerability.isDirect)}.`,
        );
      }
      if (vulnerability.fixAvailable !== false
        && !isReviewedDerivedFixProjection(vulnerability.fixAvailable, registryEvidence)) {
        fail(
          'image-size unexpectedly gained an available direct remediation: '
          + `${JSON.stringify(vulnerability.fixAvailable)}; registry evidence: `
          + `${JSON.stringify(registryEvidence)}.`,
        );
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
      const reviewedSearchProjection = name === SEARCH_LOCAL
        && isReviewedDerivedFixProjection(vulnerability.fixAvailable, registryEvidence);
      if (expected.requireNoFix
        && vulnerability.fixAvailable !== false
        && !reviewedSearchProjection) {
        fail(
          `${name} unexpectedly gained an available direct remediation: `
          + `${JSON.stringify(vulnerability.fixAvailable)}; registry evidence: `
          + `${JSON.stringify(registryEvidence)}.`,
        );
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

function expectRejectedUsingEvidence(audit, mutate, message, registryEvidence) {
  const changed = structuredClone(audit);
  mutate(changed);

  try {
    validateAudit(changed, Date.now(), registryEvidence);
  } catch {
    return;
  }

  fail(`Audit verifier self-test failed: ${message}.`);
}

function expectDependencyPolicyRejected(packageJson, packageLock, options, mutate, message) {
  const changedPackageJson = structuredClone(packageJson);
  const changedPackageLock = structuredClone(packageLock);
  mutate(changedPackageJson, changedPackageLock);

  try {
    validateDependencyPolicy(changedPackageJson, changedPackageLock, options);
  } catch {
    return;
  }

  fail(`Dependency policy self-test failed: ${message}.`);
}

function runDependencyPolicyNegativeSelfTests(packageJson, packageLock, installedLock) {
  expectDependencyPolicyRejected(
    packageJson,
    packageLock,
    {requireRoot: true},
    (_changedPackageJson, changedPackageLock) => {
      delete changedPackageLock.packages[''];
    },
    'a missing source-lock root entry was accepted',
  );
  expectDependencyPolicyRejected(
    packageJson,
    packageLock,
    {requireRoot: true},
    (_changedPackageJson, changedPackageLock) => {
      changedPackageLock.packages[''].dependencies[SEARCH_LOCAL] = '^0.55.1';
    },
    'a ranged source-lock root dependency was accepted',
  );
  expectDependencyPolicyRejected(
    packageJson,
    packageLock,
    {requireRoot: true},
    (changedPackageJson) => {
      changedPackageJson.dependencies[SEARCH_LOCAL] = '^0.55.1';
    },
    'a ranged manifest dependency was accepted',
  );
  expectDependencyPolicyRejected(
    packageJson,
    installedLock,
    {requireRoot: false},
    (_changedPackageJson, changedPackageLock) => {
      changedPackageLock.packages[`node_modules/${SEARCH_LOCAL}`].version = '0.55.2';
    },
    'an unexpected installed search package version was accepted',
  );
}

function runNegativeSelfTests(audit, registryEvidence = {}) {
  const expectRejected = (candidate, mutate, message, evidence = registryEvidence) =>
    expectRejectedUsingEvidence(candidate, mutate, message, evidence);
  const descriptorProjection = structuredClone(audit);
  descriptorProjection.vulnerabilities['@docusaurus/mdx-loader'].fixAvailable = {
    name: '@docusaurus/core',
    version: '4.0.0',
    isSemVerMajor: true,
  };
  validateAudit(descriptorProjection, Date.now(), registryEvidence);

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
      changed.vulnerabilities['@docusaurus/core'].range = 42;
    },
    'an invalid wrapper range projection was accepted',
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
  const reviewedEvidence = {
    imageSize: {
      latestVersion: IMAGE_SIZE_VERSION,
      publishedVersions: ['2.0.1', IMAGE_SIZE_VERSION],
    },
    searchLocal: {
      latestVersion: SEARCH_LOCAL_VERSION,
      publishedVersions: [
        SEARCH_LOCAL_DERIVED_FIX.version,
        '0.55.2',
        '0.55.3-beta.1',
        SEARCH_LOCAL_VERSION,
      ],
    },
  };
  expectRejected(
    audit,
    () => {},
    'the image-size exception was accepted without image-size registry evidence',
    {searchLocal: reviewedEvidence.searchLocal},
  );
  for (const [imageSize, message] of [
    [
      {latestVersion: '2.0.3', publishedVersions: [IMAGE_SIZE_VERSION, '2.0.3']},
      'the image-size exception was accepted after the latest tag moved',
    ],
    [
      {latestVersion: IMAGE_SIZE_VERSION, publishedVersions: [IMAGE_SIZE_VERSION, '2.0.3']},
      'the image-size exception was accepted with a newer stable release',
    ],
    [
      {latestVersion: IMAGE_SIZE_VERSION, publishedVersions: [IMAGE_SIZE_VERSION, '2.1.0-beta.1']},
      'the image-size exception was accepted with a newer prerelease core',
    ],
    [
      {latestVersion: IMAGE_SIZE_VERSION, publishedVersions: ['2.0.1']},
      'the image-size exception was accepted without the reviewed release',
    ],
    [
      {latestVersion: IMAGE_SIZE_VERSION, publishedVersions: [IMAGE_SIZE_VERSION, 'not-semver']},
      'the image-size exception was accepted with malformed version evidence',
    ],
  ]) {
    expectRejected(
      audit,
      () => {},
      message,
      {...reviewedEvidence, imageSize},
    );
  }
  if (audit.vulnerabilities[SEARCH_LOCAL]) {
    const reviewedImageProjection = structuredClone(audit);
    reviewedImageProjection.vulnerabilities['image-size'].fixAvailable = SEARCH_LOCAL_DERIVED_FIX;
    reviewedImageProjection.vulnerabilities[SEARCH_LOCAL].fixAvailable = false;
    validateAudit(reviewedImageProjection, Date.now(), reviewedEvidence);

    expectRejected(
      reviewedImageProjection,
      () => {},
      'the reviewed image-size downgrade was accepted without search-local registry evidence',
      {imageSize: reviewedEvidence.imageSize},
    );
    expectRejected(
      reviewedImageProjection,
      () => {},
      'the reviewed image-size downgrade was accepted when its target was unpublished',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: SEARCH_LOCAL_VERSION,
          publishedVersions: ['0.55.2', SEARCH_LOCAL_VERSION],
        },
      },
    );
    expectRejected(
      reviewedImageProjection,
      () => {},
      'the reviewed image-size downgrade was accepted with malformed registry evidence',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: SEARCH_LOCAL_VERSION,
          publishedVersions: [
            SEARCH_LOCAL_DERIVED_FIX.version,
            SEARCH_LOCAL_VERSION,
            'not-semver',
          ],
        },
      },
    );
    expectRejected(
      reviewedImageProjection,
      () => {},
      'the reviewed image-size downgrade was accepted with a newer registry version',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: SEARCH_LOCAL_VERSION,
          publishedVersions: [
            SEARCH_LOCAL_DERIVED_FIX.version,
            SEARCH_LOCAL_VERSION,
            '0.56.0-beta.1',
          ],
        },
      },
    );
    expectRejected(
      audit,
      (changed) => {
        changed.vulnerabilities['image-size'].fixAvailable = true;
      },
      'a boolean image-size remediation was accepted',
      reviewedEvidence,
    );
    for (const [projection, message] of [
      [{...SEARCH_LOCAL_DERIVED_FIX, version: SEARCH_LOCAL_VERSION},
        'an image-size remediation to the installed version was accepted'],
      [{...SEARCH_LOCAL_DERIVED_FIX, version: '0.55.4'},
        'a future image-size remediation was accepted'],
      [{...SEARCH_LOCAL_DERIVED_FIX, name: '@docusaurus/core'},
        'an image-size downgrade with changed package name was accepted'],
      [{...SEARCH_LOCAL_DERIVED_FIX, isSemVerMajor: false},
        'an image-size downgrade with changed major classification was accepted'],
    ]) {
      expectRejected(
        audit,
        (changed) => {
          changed.vulnerabilities['image-size'].fixAvailable = projection;
        },
        message,
        reviewedEvidence,
      );
    }

    const reviewedProjection = structuredClone(audit);
    reviewedProjection.vulnerabilities[SEARCH_LOCAL].fixAvailable = SEARCH_LOCAL_DERIVED_FIX;
    reviewedProjection.vulnerabilities['image-size'].fixAvailable = false;
    validateAudit(reviewedProjection, Date.now(), reviewedEvidence);

    expectRejected(
      reviewedProjection,
      () => {},
      'the reviewed search-local downgrade was accepted without search-local registry evidence',
      {imageSize: reviewedEvidence.imageSize},
    );
    expectRejected(
      reviewedProjection,
      () => {},
      'the reviewed search-local downgrade was accepted when the latest tag moved',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: '0.55.4',
          publishedVersions: [
            SEARCH_LOCAL_DERIVED_FIX.version,
            SEARCH_LOCAL_VERSION,
            '0.55.4',
          ],
        },
      },
    );
    expectRejected(
      reviewedProjection,
      () => {},
      'the reviewed search-local downgrade was accepted with a newer stable registry version',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: SEARCH_LOCAL_VERSION,
          publishedVersions: [
            SEARCH_LOCAL_DERIVED_FIX.version,
            SEARCH_LOCAL_VERSION,
            '0.55.4',
          ],
        },
      },
    );
    expectRejected(
      reviewedProjection,
      () => {},
      'the reviewed search-local downgrade was accepted with a newer prerelease version',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: SEARCH_LOCAL_VERSION,
          publishedVersions: [
            SEARCH_LOCAL_DERIVED_FIX.version,
            SEARCH_LOCAL_VERSION,
            '0.56.0-beta.1',
          ],
        },
      },
    );
    expectRejected(
      reviewedProjection,
      () => {},
      'the reviewed search-local downgrade was accepted with malformed registry evidence',
      {
        ...reviewedEvidence,
        searchLocal: {
          latestVersion: SEARCH_LOCAL_VERSION,
          publishedVersions: [
            SEARCH_LOCAL_DERIVED_FIX.version,
            SEARCH_LOCAL_VERSION,
            'not-semver',
          ],
        },
      },
    );
    expectRejected(
      audit,
      (changed) => {
        changed.vulnerabilities[SEARCH_LOCAL].fixAvailable = true;
      },
      'a boolean search-local remediation was accepted',
      reviewedEvidence,
    );
    expectRejected(
      audit,
      (changed) => {
        changed.vulnerabilities[SEARCH_LOCAL].fixAvailable = {
          name: SEARCH_LOCAL,
          version: SEARCH_LOCAL_VERSION,
          isSemVerMajor: false,
        };
      },
      'a search-local remediation to the installed version was accepted',
      reviewedEvidence,
    );
    expectRejected(
      audit,
      (changed) => {
        changed.vulnerabilities[SEARCH_LOCAL].fixAvailable = {
          name: SEARCH_LOCAL,
          version: '0.55.4',
          isSemVerMajor: false,
        };
      },
      'a future search-local remediation was accepted',
      reviewedEvidence,
    );
    expectRejected(
      audit,
      (changed) => {
        changed.vulnerabilities[SEARCH_LOCAL].fixAvailable = {
          ...SEARCH_LOCAL_DERIVED_FIX,
          isSemVerMajor: false,
        };
      },
      'a search-local downgrade with changed major classification was accepted',
      reviewedEvidence,
    );
    expectRejected(
      audit,
      (changed) => {
        changed.vulnerabilities[SEARCH_LOCAL].fixAvailable = {
          ...SEARCH_LOCAL_DERIVED_FIX,
          name: '@docusaurus/core',
        };
      },
      'a search-local downgrade with changed package name was accepted',
      reviewedEvidence,
    );
  }
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/mdx-loader'].fixAvailable = {
        name: 'unreviewed-package',
        version: '1.0.0',
        isSemVerMajor: false,
      };
    },
    'a remediation descriptor outside the exact closure was accepted',
  );
  expectRejected(
    audit,
    (changed) => {
      changed.vulnerabilities['@docusaurus/mdx-loader'].fixAvailable = {
        name: '@docusaurus/core',
        version: '4.0.0',
        isSemVerMajor: true,
        unexpected: true,
      };
    },
    'a malformed remediation descriptor was accepted',
  );
}

function createNpmInvocation({
  platform = process.platform,
  nodeExecutable = process.execPath,
  npmExecPath = process.env.npm_execpath,
} = {}) {
  if (typeof npmExecPath === 'string' && npmExecPath.trim().length > 0) {
    return {command: nodeExecutable, argumentPrefix: [npmExecPath]};
  }
  if (platform === 'win32') {
    fail(
      'Cannot execute npm safely on Windows without npm_execpath; '
      + 'run this verifier through an npm script.',
    );
  }
  return {command: 'npm', argumentPrefix: []};
}

function invokeNpm(invocation, npmArguments, options, spawn = spawnSync) {
  return spawn(
    invocation.command,
    [...invocation.argumentPrefix, ...npmArguments],
    options,
  );
}

function runNpmInvocationSelfTests() {
  const npmExecPath = 'C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js';
  const windowsInvocation = createNpmInvocation({
    platform: 'win32',
    nodeExecutable: 'C:\\Program Files\\nodejs\\node.exe',
    npmExecPath,
  });
  let captured;
  invokeNpm(
    windowsInvocation,
    ['audit', '--json'],
    {env: {PATH: 'C:\\fake npm path'}},
    (command, args, options) => {
      captured = {command, args, options};
      return {status: 0};
    },
  );
  if (captured.command !== 'C:\\Program Files\\nodejs\\node.exe'
    || !sameOrderedArray(captured.args, [npmExecPath, 'audit', '--json'])) {
    fail('npm invocation self-test split or changed the Windows npm_execpath argument.');
  }
  if (sameOrderedArray([npmExecPath, '--json', 'audit'], [npmExecPath, 'audit', '--json'])) {
    fail('npm invocation self-test accepted reordered Windows arguments.');
  }

  let rejectedMissingWindowsExecPath = false;
  try {
    createNpmInvocation({platform: 'win32', npmExecPath: undefined});
  } catch (error) {
    rejectedMissingWindowsExecPath = error.message.includes('without npm_execpath');
  }
  if (!rejectedMissingWindowsExecPath) {
    fail('npm invocation self-test accepted Windows without npm_execpath.');
  }

  const linuxInvocation = createNpmInvocation({platform: 'linux', npmExecPath: undefined});
  captured = undefined;
  invokeNpm(
    linuxInvocation,
    ['audit', '--json'],
    {env: {PATH: '/tmp/fake-npm-bin'}},
    (command, args, options) => {
      captured = {command, args, options};
      return {status: 0};
    },
  );
  if (captured.command !== 'npm'
    || !sameOrderedArray(captured.args, ['audit', '--json'])
    || captured.options.env.PATH !== '/tmp/fake-npm-bin') {
    fail('npm invocation self-test broke the PATH-injected non-Windows fallback.');
  }
}

function readNpmRegistryField(npmInvocation, websiteRoot, packageName, field) {
  const viewProcess = invokeNpm(
    npmInvocation,
    [
      'view',
      packageName,
      field,
      '--json',
      '--prefer-online',
      '--registry=https://registry.npmjs.org/',
    ],
    {
      cwd: websiteRoot,
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
      timeout: 30_000,
    },
  );
  if (viewProcess.error) {
    fail(`Could not query npm registry field ${packageName} ${field}: ${viewProcess.error.message}`);
  }
  if (viewProcess.status !== 0) {
    fail(
      `npm registry query for ${packageName} ${field} exited `
      + `${viewProcess.status}: ${viewProcess.stderr}`,
    );
  }
  try {
    return JSON.parse(viewProcess.stdout);
  } catch (error) {
    fail(`npm registry returned invalid JSON for ${packageName} ${field}: ${error.message}`);
  }
}

function loadPackageRegistryEvidence(npmInvocation, websiteRoot, packageName) {
  return {
    latestVersion: readNpmRegistryField(
      npmInvocation,
      websiteRoot,
      packageName,
      'dist-tags.latest',
    ),
    publishedVersions: readNpmRegistryField(
      npmInvocation,
      websiteRoot,
      packageName,
      'versions',
    ),
  };
}

function loadRegistryEvidence(npmInvocation, websiteRoot) {
  const registryEvidence = {
    imageSize: loadPackageRegistryEvidence(npmInvocation, websiteRoot, IMAGE_SIZE),
    searchLocal: loadPackageRegistryEvidence(npmInvocation, websiteRoot, SEARCH_LOCAL),
  };
  if (!hasReviewedImageSizeRegistryEvidence(registryEvidence)) {
    fail(
      `${IMAGE_SIZE} registry state changed: expected latest version `
      + `${IMAGE_SIZE_VERSION} with no newer published version; received `
      + `${JSON.stringify(registryEvidence.imageSize)}.`,
    );
  }
  if (!hasReviewedSearchLocalRegistryEvidence(registryEvidence)) {
    fail(
      `${SEARCH_LOCAL} registry state changed: expected latest version `
      + `${SEARCH_LOCAL_VERSION} with no newer published version; received `
      + `${JSON.stringify(registryEvidence.searchLocal)}.`,
    );
  }
  return registryEvidence;
}

export {
  createNpmInvocation,
  runDependencyPolicyNegativeSelfTests,
  runNegativeSelfTests,
  runNpmInvocationSelfTests,
  validateAudit,
  validateDependencyPolicy,
};

if (process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))) {
  try {
    const websiteRoot = process.cwd();
    const installedLockPath = resolve(websiteRoot, 'node_modules', '.package-lock.json');
    if (!existsSync(installedLockPath)) {
      fail('Installed-tree audit requires `npm ci` and node_modules/.package-lock.json.');
    }
    const packageJson = readJson(resolve(websiteRoot, 'package.json'));
    const packageLock = readJson(resolve(websiteRoot, 'package-lock.json'));
    const installedLock = readJson(installedLockPath);
    validateDependencyPolicy(packageJson, packageLock, {requireRoot: true});
    validateDependencyPolicy(packageJson, installedLock, {requireRoot: false});
    runDependencyPolicyNegativeSelfTests(packageJson, packageLock, installedLock);
    runNpmInvocationSelfTests();
    const npmInvocation = createNpmInvocation();
    const registryEvidence = loadRegistryEvidence(npmInvocation, websiteRoot);
    const auditProcess = invokeNpm(npmInvocation, ['audit', '--json'], {
      cwd: websiteRoot,
      encoding: 'utf8',
      maxBuffer: 32 * 1024 * 1024,
    });
    if (auditProcess.error) fail(`Could not execute npm audit: ${auditProcess.error.message}`);
    if (![0, 1].includes(auditProcess.status)) {
      fail(`npm audit exited ${auditProcess.status}: ${auditProcess.stderr}`);
    }
    const audit = JSON.parse(auditProcess.stdout);
    const profileName = validateAudit(audit, Date.now(), registryEvidence);
    runNegativeSelfTests(audit, registryEvidence);
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
