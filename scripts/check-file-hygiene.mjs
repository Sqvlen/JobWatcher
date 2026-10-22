#!/usr/bin/env node
/**
 * Repo-wide file hygiene guard. Two checks, both fatal:
 *
 * 1. No line may end with trailing whitespace. The compiler and the IDE do not
 *    flag it in .md, .json, .yml, .csproj or Dockerfile, so nothing else catches it.
 *
 * 2. No path may contain a backslash. On macOS and Linux "\" is a legal filename
 *    character, so a tool that writes a Windows-style path creates a single
 *    directory literally named `bin\Debug` instead of `bin/Debug`. Such a path
 *    slips past .gitignore (`bin/` does not match it) and past MSBuild's
 *    DefaultItemExcludes, and it breaks MSBuild glob expansion outright — the
 *    SDK then hands csc a literal `**\/*.cs`, failing the build with CS2021.
 *
 * Ported from qbeauty-ua without its third check ("no file ends with a newline"):
 * the files here keep their final newline.
 *
 * Usage:  node scripts/check-file-hygiene.mjs [--fix]
 *         --fix strips trailing whitespace. Backslash paths are reported only:
 *         removing or renaming them is a deliberate call, not an automatic one.
 */

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

const BINARY_EXTENSIONS = new Set([
  '.png', '.jpg', '.jpeg', '.gif', '.ico', '.webp', '.bmp', '.avif',
  '.woff', '.woff2', '.ttf', '.otf', '.eot',
  '.pdf', '.zip', '.gz', '.tar', '.7z',
  '.mp3', '.mp4', '.webm', '.mov', '.wav',
  '.dll', '.exe', '.so', '.dylib', '.pdb', '.snk',
]);

const fix = process.argv.includes('--fix');

const git = (...args) =>
  execFileSync('git', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
    .split('\0')
    .filter(Boolean);

const extensionOf = (path) => {
  const dot = path.lastIndexOf('.');
  const slash = path.lastIndexOf('/');

  return dot > slash ? path.slice(dot).toLowerCase() : '';
};

const hasTrailingWhitespace = (text) => /[ \t]+(?:\r?\n|$)/.test(text);

const stripTrailingWhitespace = (text) => text.replace(/[ \t]+(?=\r?\n|$)/g, '');

const trackedFiles = git('ls-files', '-z');

// --directory collapses a wholly untracked directory into one entry, so a stray
// `bin\Debug` is reported as the directory itself rather than its every file.
const untrackedEntries = git('ls-files', '--others', '--exclude-standard', '--directory', '-z');

/* ---------- check 1: trailing whitespace (tracked files only) ---------- */

const whitespaceOffenders = [];

for (const path of trackedFiles) {
  if (BINARY_EXTENSIONS.has(extensionOf(path))) {
    continue;
  }

  let buffer;

  try {
    buffer = readFileSync(path);
  } catch {
    continue; // deleted or unreadable in this working tree
  }

  if (buffer.length === 0 || buffer.subarray(0, 1024).includes(0)) {
    continue; // empty or binary
  }

  const text = buffer.toString('utf8');

  if (!hasTrailingWhitespace(text)) {
    continue;
  }

  whitespaceOffenders.push(path);

  if (fix) {
    writeFileSync(path, stripTrailingWhitespace(text));
  }
}

/* ---------- check 2: backslash in path (tracked and untracked alike) ---------- */

const backslashPaths = [...trackedFiles, ...untrackedEntries].filter((path) => path.includes('\\'));

/* ---------- report ---------- */

let failed = false;

if (whitespaceOffenders.length === 0) {
  console.log(`✔ ${trackedFiles.length} tracked files checked — no line ends with trailing whitespace`);
} else if (fix) {
  console.log(`✔ stripped trailing whitespace from ${whitespaceOffenders.length} file(s)`);
} else {
  for (const path of whitespaceOffenders) {
    console.error(`${path}:  line(s) end with trailing whitespace`);
  }

  console.error(`\n✖ ${whitespaceOffenders.length} file(s) have trailing whitespace — run: node scripts/check-file-hygiene.mjs --fix`);
  failed = true;
}

if (backslashPaths.length === 0) {
  console.log('✔ no path contains a backslash');
} else {
  for (const path of backslashPaths) {
    console.error(`\n${path}:  path contains a backslash`);
  }

  console.error(
    '\n✖ a backslash in a path is a Windows-style path written literally on Unix.' +
      '\n  It escapes .gitignore and breaks MSBuild glob expansion (CS2021 on `**/*.cs`).' +
      '\n  Delete it if it is build output, rename it otherwise — this is not auto-fixed.',
  );
  failed = true;
}

process.exit(failed ? 1 : 0);
