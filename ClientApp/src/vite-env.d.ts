/// <reference types="vite/client" />

// Vite's ambient module types. Chiefly here so a theme can `import wall from './keep-wall.jpg'`
// and get back a hashed URL: the build then emits the file under /assets alongside the bundles,
// where it is content-addressed and cached immutably, and an unused image cannot survive the
// theme that referenced it.
