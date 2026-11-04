// Static guards on the shipped frontend: it must stay free of the things the Content-Security-Policy forbids, of
// markup-writing DOM calls, and of any third-party origin.
import assert from 'node:assert/strict';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'src', 'ArbetsWatch.Web', 'wwwroot');

function files(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

const all = files(root).map((path) => ({ name: relative(root, path), text: readFileSync(path, 'utf8') }));
const scripts = all.filter((f) => f.name.endsWith('.js'));
const html = all.filter((f) => f.name.endsWith('.html'));

test('no script writes markup or evaluates text', () => {
  const forbidden = /innerHTML|outerHTML|insertAdjacentHTML|setHTML|createContextualFragment|document\.write|\beval\(|new Function|setTimeout\(['"`]|srcdoc|DOMParser/;
  for (const { name, text } of scripts) {
    assert.ok(!forbidden.test(text), name);
  }
});

test('the page has no inline script, style or event handler', () => {
  for (const { name, text } of html) {
    assert.ok(!/<style/i.test(text), `${name}: <style>`);
    assert.ok(!/\sstyle=/i.test(text), `${name}: style attribute`);
    assert.ok(!/\son[a-z]+=/i.test(text), `${name}: event handler attribute`);
    for (const [tag] of text.matchAll(/<script\b[^>]*>/gi)) {
      assert.ok(/\ssrc="\//.test(tag), `${name}: inline script ${tag}`);
    }
  }
});

test('nothing loads from another origin', () => {
  for (const { name, text } of html) {
    for (const [, url] of text.matchAll(/(?:src|href)="([^"]+)"/gi)) {
      assert.ok(url.startsWith('/'), `${name}: ${url}`);
    }
  }
  for (const { name, text } of all.filter((f) => f.name.endsWith('.css'))) {
    assert.ok(!/url\(|@import/.test(text), name);
  }
  for (const { name, text } of scripts) {
    const urls = text.match(/https?:\/\/[^\s'"`]+/g) ?? [];
    assert.deepEqual(urls.filter((u) => !u.includes('arbetsformedlingen')), [], name);
  }
});

test('every link that leaves the page drops the opener and the referrer', () => {
  for (const { name, text } of html) {
    for (const [tag] of text.matchAll(/<a\b[^>]*target=[^>]*>/gi)) {
      assert.ok(tag.includes('noopener') && tag.includes('noreferrer'), `${name}: ${tag}`);
    }
  }
});

test('links built in script drop the opener and the referrer too', () => {
  const app = scripts.find((f) => f.name.endsWith('app.js')).text;
  assert.ok(app.includes("anchor.rel = 'noopener noreferrer'"));
  assert.equal(app.match(/\.target = /g).length, 1, 'every external link goes through externalAnchor');
});

test('no key or token handling exists in the frontend', () => {
  for (const { name, text } of all) {
    assert.ok(!/DeepL-Auth-Key|authorization|api[_-]?key/i.test(text), name);
    assert.ok(!/document\.cookie/.test(text), name);
  }
});
