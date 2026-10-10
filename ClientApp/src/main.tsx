import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App'
import { bootTheme } from './theme'

// Before anything renders. Every colour, face and silhouette in the app resolves through the
// custom properties this writes, so a render that happened first would be a render with none of
// them set. It reads localStorage synchronously and touches only `<html>`, so it costs a frame of
// nothing — and it is why switching themes never shows you the old one on the way to the new one.
bootTheme()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
